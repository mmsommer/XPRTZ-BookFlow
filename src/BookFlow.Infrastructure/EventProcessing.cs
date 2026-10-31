using System.Data;
using BookFlow.Core;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.Infrastructure;

public interface IEventTransaction
{
    Task ExecuteAsync(string consumer, DomainEvent message,
        Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}

public sealed class EventTransaction(BookFlowDbContext db) : IEventTransaction
{
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

public static class EventLookup
{
    public static async Task<Manuscript> ManuscriptAsync(BookFlowDbContext db, DomainEvent message, CancellationToken ct) =>
        await db.Manuscripts.SingleOrDefaultAsync(x => x.Id == message.Data.ManuscriptId &&
            x.TenantId == message.Data.TenantId, ct)
        ?? throw new PermanentFailureException("Event manuscript does not exist in this tenant.");

    public static async Task<Book> BookAsync(BookFlowDbContext db, DomainEvent message, CancellationToken ct) =>
        await db.Books.SingleOrDefaultAsync(x => x.Id == message.Data.BookId && x.TenantId == message.Data.TenantId, ct)
        ?? throw new PermanentFailureException("Event book does not exist in this tenant.");

    public static void RequireType(DomainEvent message, params string[] types)
    {
        message.Validate();
        if (!types.Contains(message.EventType))
            throw new PermanentFailureException("Unsupported consumer event type.");
    }
}
