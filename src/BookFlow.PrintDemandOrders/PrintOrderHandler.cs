using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.PrintDemandOrders;

public sealed class PrintOrderHandler(BookFlowDbContext db, IEventTransaction transaction, IPrintProvider provider) : IEventHandler
{
    public string Consumer => "print-demand-orders";

    public Task HandleAsync(DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Approved, EventTypes.Order);
        return transaction.ExecuteAsync(Consumer, message, async token =>
        {
            var book = await EventLookup.BookAsync(db, message, token);
            var order = await db.PrintOrders.SingleOrDefaultAsync(x => x.Id == message.Data.PrintOrderId && x.BookId == book.Id, token)
                ?? throw new PermanentFailureException("Print order not found.");
            if (order.ExternalReference is not null)
                return;
            if (!book.Orderable || order.Quantity < 1 ||
                !await db.Participants.AnyAsync(x => x.Id == order.DistributorId &&
                    x.TenantId == book.TenantId && x.Role == "Distributor" && x.Active, token))
                throw new PermanentFailureException("Invalid book, distributor, or print quantity.");
            var reference = await provider.OrderAsync(new PrintSubmission(book.Id, book.Title, book.Isbn,
                order.DistributorId, order.Quantity), $"print-order:{order.Id}", token);
            if (string.IsNullOrWhiteSpace(reference))
                throw new PermanentFailureException("Provider returned an empty order reference.");
            order.ExternalReference = reference;
            order.Status = PrintOrderStatus.Submitted;
        }, ct);
    }
}
