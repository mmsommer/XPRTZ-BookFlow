using System.Data;
using System.Text.Json;
using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.Notifications;

/// <summary>Durably accepts validated notification event batches.</summary>
public interface INotificationAcceptance
{
    /// <summary>Validates and durably queues a batch of notification events.</summary>
    /// <param name="messages">The notification events to accept durably.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task AcceptAsync(DomainEvent[] messages, CancellationToken ct);
}

/// <summary>Persists validated notification events before recipient resolution.</summary>
/// <param name="db">The publishing workflow database context.</param>
public sealed class NotificationAcceptance(BookFlowDbContext db) : INotificationAcceptance
{
    /// <summary>Validates and durably queues a batch of notification events.</summary>
    /// <param name="messages">The notification events to accept durably.</param>
    /// <param name="ct">Cancels the operation.</param>
    public async Task AcceptAsync(DomainEvent[] messages, CancellationToken ct)
    {
        if (messages.Length is < 1 or > 100)
            throw new ArgumentException("Expected between 1 and 100 notifications.");
        foreach (var message in messages)
        {
            EventLookup.RequireType(message, EventTypes.Notifications);
            if (message.EventType is EventTypes.Assigned or EventTypes.Edited or EventTypes.Revised or EventTypes.Approved or EventTypes.Proofprint &&
                message.Data.ManuscriptId is null)
                throw new PermanentFailureException("Notification manuscript is required.");
            if (message.EventType == EventTypes.Proofprint &&
                (message.Data.VersionId is null || message.Data.ProofprintId is null))
                throw new PermanentFailureException("Notification proofprint and version are required.");
            if (message.EventType is EventTypes.Approved or EventTypes.Available or EventTypes.LowStock or EventTypes.Depleted &&
                message.Data.BookId is null)
                throw new PermanentFailureException("Notification book is required.");
            if (message.EventType == EventTypes.Available && string.IsNullOrWhiteSpace(message.Data.ListingUrl))
                throw new PermanentFailureException("Distributor listing URL is required.");
        }
        foreach (var group in messages.GroupBy(x => x.Id))
        {
            if (group.Select(x => JsonSerializer.Serialize(x, EventJson.Options)).Distinct().Count() != 1)
                throw new ConflictException("A notification batch reused an event ID with different payloads.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        foreach (var message in messages.DistinctBy(x => x.Id))
        {
            var payload = JsonSerializer.Serialize(message, EventJson.Options);
            var existing = await db.LocalDeliveries.SingleOrDefaultAsync(x => x.Consumer == "notifications" && x.EventId == message.Id, ct);
            if (existing is not null)
            {
                if (existing.Payload != payload)
                    throw new ConflictException("Notification event ID was reused with a different payload.");
                continue;
            }
            db.LocalDeliveries.Add(new LocalDelivery { Consumer = "notifications", EventId = message.Id, Payload = payload });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
