using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BookFlow.ApiFrontend;

/// <summary>Validates and durably accepts authorized distributor callbacks.</summary>
public interface ICallbackService
{
    /// <summary>Validates an authorized callback and durably queues it without duplicating matching retries.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task AcceptAsync(Actor actor, DomainEvent message, CancellationToken ct);
}

/// <summary>Validates distributor callback scope and persists deduplicated outbox events.</summary>
/// <param name="db">The publishing workflow database context.</param>
/// <param name="config">The application configuration.</param>
public sealed class CallbackService(BookFlowDbContext db, IConfiguration config) : ICallbackService
{
    /// <summary>Validates an authorized callback and durably queues it without duplicating matching retries.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    public async Task AcceptAsync(Actor actor, DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Callbacks);
        if (actor.TenantId != message.Data.TenantId)
            throw new AccessDeniedException();
        if (message.EventType is EventTypes.Printed or EventTypes.Delivered)
            actor.Require("PrintProvider");
        else
        {
            actor.Require("Distributor");
            if (actor.Id != message.Data.DistributorId)
                throw new AccessDeniedException();
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var book = await EventLookup.BookAsync(db, message, ct);
        if (message.Data.DistributorId is not { } distributor ||
            !await db.Participants.AnyAsync(x => x.TenantId == book.TenantId && x.Id == distributor &&
                x.Active && x.Role == "Distributor", ct))
            throw new PermanentFailureException("Invalid distributor callback.");
        if (message.EventType != EventTypes.Available && (message.Data.Quantity is null or < 0 or > 1000000))
            throw new PermanentFailureException("Callback quantity is required and must be nonnegative.");
        if (message.EventType == EventTypes.LowStock && message.Data.Quantity != config.GetValue<int>("Inventory:LowStockThreshold", 10) ||
            message.EventType == EventTypes.Depleted && message.Data.Quantity != 0)
            throw new PermanentFailureException("Callback alert does not match its stock threshold.");
        if (message.EventType is EventTypes.Printed or EventTypes.Delivered &&
            !await db.PrintOrders.AnyAsync(x => x.Id == message.Data.PrintOrderId &&
                x.BookId == book.Id && x.DistributorId == distributor, ct))
            throw new PermanentFailureException("Production callback must reference an order for this distributor.");
        if (message.EventType == EventTypes.Available)
        {
            if (!Uri.TryCreate(message.Data.ListingUrl, UriKind.Absolute, out var listing) ||
                listing.Scheme != "https" || !string.IsNullOrEmpty(listing.UserInfo) ||
                !(config.GetSection("BookFlow:ListingHosts").Get<string[]>() ?? []).Contains(listing.Host, StringComparer.OrdinalIgnoreCase))
                throw new PermanentFailureException("Distributor listing host is not approved.");
        }
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"callback:{actor.TenantId}:{actor.Id}:{message.Id}")));
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(message, EventJson.Options)));
        var existing = await db.Idempotency.FindAsync([key], ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash)
                throw new ConflictException("Callback ID reused with different payload.");
            return;
        }
        if (await db.Outbox.AnyAsync(x => x.Id == message.Id, ct))
            throw new ConflictException("Event ID already belongs to another producer.");
        db.Idempotency.Add(new IdempotencyRecord { Key = key, RequestHash = hash, ResourceId = book.Id });
        db.Enqueue(message);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
