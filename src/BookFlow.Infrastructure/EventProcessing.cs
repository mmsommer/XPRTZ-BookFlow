using System.Data;
using BookFlow.Core;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.Infrastructure;

/// <summary>Executes a consumer action with durable inbox deduplication.</summary>
public interface IEventTransaction
{
    /// <summary>Commits the consumer action and inbox acknowledgement once per event.</summary>
    /// <param name="consumer">The consumer name used for routing and deduplication.</param>
    /// <param name="message">The domain event to process.</param>
    /// <param name="action">The consumer changes to execute within the transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task ExecuteAsync(string consumer, DomainEvent message,
        Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}

/// <summary>Commits consumer changes and inbox acknowledgement in a serializable transaction.</summary>
/// <param name="db">The publishing workflow database context.</param>
public sealed class EventTransaction(BookFlowDbContext db) : IEventTransaction
{
    /// <summary>Commits the consumer action and inbox acknowledgement once per event.</summary>
    /// <param name="consumer">The consumer name used for routing and deduplication.</param>
    /// <param name="message">The domain event to process.</param>
    /// <param name="action">The consumer changes to execute within the transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task ExecuteAsync(string consumer, DomainEvent message,
        Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        message.Validate();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await db.Inbox.AnyAsync(x => x.Consumer == consumer && x.EventId == message.Id, cancellationToken))
            return;
        await action(cancellationToken);
        db.Inbox.Add(new InboxMessage { Consumer = consumer, EventId = message.Id, ProcessedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Resolves tenant-scoped event aggregates and validates consumer event types.</summary>
public static class EventLookup
{
    /// <summary>Resolves the event's manuscript within its publisher tenant.</summary>
    /// <param name="db">The publishing workflow database context.</param>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    public static async Task<Manuscript> ManuscriptAsync(BookFlowDbContext db, DomainEvent message, CancellationToken ct) =>
        await db.Manuscripts.SingleOrDefaultAsync(x => x.Id == message.Data.ManuscriptId &&
            x.TenantId == message.Data.TenantId, ct)
        ?? throw new PermanentFailureException("Event manuscript does not exist in this tenant.");

    /// <summary>Resolves the event's book within its publisher tenant.</summary>
    /// <param name="db">The publishing workflow database context.</param>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    public static async Task<Book> BookAsync(BookFlowDbContext db, DomainEvent message, CancellationToken ct) =>
        await db.Books.SingleOrDefaultAsync(x => x.Id == message.Data.BookId && x.TenantId == message.Data.TenantId, ct)
        ?? throw new PermanentFailureException("Event book does not exist in this tenant.");

    /// <summary>Validates the event and requires one of the permitted event types.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="types">The event types permitted for this consumer.</param>
    public static void RequireType(DomainEvent message, params string[] types)
    {
        message.Validate();
        if (!types.Contains(message.EventType))
            throw new PermanentFailureException("Unsupported consumer event type.");
    }
}
