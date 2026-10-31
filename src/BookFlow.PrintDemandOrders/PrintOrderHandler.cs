using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.PrintDemandOrders;

/// <summary>Submits idempotent print orders for approval and print-request events.</summary>
/// <param name="db">The publishing workflow database context.</param>
/// <param name="transaction">The deduplicating consumer transaction service.</param>
/// <param name="provider">The print provider used for proofprints or print orders.</param>
public sealed class PrintOrderHandler(BookFlowDbContext db, IEventTransaction transaction, IPrintProvider provider) : IEventHandler
{
    /// <summary>The event consumer name.</summary>
    public string Consumer => "print-demand-orders";

    /// <summary>Submits the event's print order once and records the provider reference.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    public async Task HandleAsync(DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Approved, EventTypes.Order);
        if (await db.Inbox.AnyAsync(x => x.Consumer == Consumer && x.EventId == message.Id, ct))
            return;

        var book = await EventLookup.BookAsync(db, message, ct);
        var order = await db.PrintOrders.SingleOrDefaultAsync(
            x => x.Id == message.Data.PrintOrderId && x.BookId == book.Id, ct)
            ?? throw new PermanentFailureException("Print order not found.");
        if (order.ExternalReference is not null)
        {
            await transaction.ExecuteAsync(Consumer, message, _ => Task.CompletedTask, ct);
            return;
        }
        if (!book.Orderable || order.Quantity < 1 ||
            !await db.Participants.AnyAsync(x => x.Id == order.DistributorId &&
                x.TenantId == book.TenantId && x.Role == "Distributor" && x.Active, ct))
            throw new PermanentFailureException("Invalid book, distributor, or print quantity.");

        var reference = await provider.OrderAsync(new PrintSubmission(book.Id, book.Title, book.Isbn,
            order.DistributorId, order.Quantity), $"print-order:{order.Id}", ct);
        if (string.IsNullOrWhiteSpace(reference))
            throw new PermanentFailureException("Provider returned an empty order reference.");

        await transaction.ExecuteAsync(Consumer, message, async token =>
        {
            var current = await db.PrintOrders.SingleAsync(x => x.Id == order.Id && x.BookId == book.Id, token);
            await db.Entry(current).ReloadAsync(token);
            current.ExternalReference ??= reference;
            if (current.Status < PrintOrderStatus.Submitted)
                current.Status = PrintOrderStatus.Submitted;
        }, ct);
    }
}
