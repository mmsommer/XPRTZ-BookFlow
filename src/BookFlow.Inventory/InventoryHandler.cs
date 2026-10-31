using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookFlow.Inventory;

public static class InventoryRules
{
    public static int AvailableAfter(string type, int current, int quantity) => type switch
    {
        EventTypes.Delivered => checked(current + quantity),
        EventTypes.Distributed or EventTypes.Sold => current >= quantity
            ? current - quantity : throw new PermanentFailureException("Inventory cannot be negative."),
        EventTypes.Stock => quantity,
        EventTypes.Printed or EventTypes.LowStock or EventTypes.Depleted => current,
        _ => throw new PermanentFailureException("Unsupported inventory event.")
    };
}

public sealed class InventoryHandler(BookFlowDbContext db, IEventTransaction transaction, IOptions<InventoryOptions> options) : IEventHandler
{
    public string Consumer => "inventory";

    public Task HandleAsync(DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Subscriptions[Consumer]);
        return transaction.ExecuteAsync(Consumer, message, async token =>
        {
            var book = await EventLookup.BookAsync(db, message, token);
            var distributor = message.Data.DistributorId;
            if (string.IsNullOrWhiteSpace(distributor) || message.Data.Quantity is not { } quantity || quantity < 0 ||
                !await db.Participants.AnyAsync(x => x.Id == distributor && x.TenantId == book.TenantId &&
                    x.Active && x.Role == "Distributor", token))
                throw new PermanentFailureException("Invalid inventory quantity or distributor.");
            if (message.EventType == EventTypes.LowStock && quantity != options.Value.LowStockThreshold ||
                message.EventType == EventTypes.Depleted && quantity != 0)
                throw new PermanentFailureException("Invalid stock alert threshold.");
            var stock = await db.Stock.SingleOrDefaultAsync(x => x.BookId == book.Id && x.DistributorId == distributor, token);
            if (stock is null)
            {
                stock = new StockRecord { BookId = book.Id, DistributorId = distributor };
                db.Stock.Add(stock);
            }
            if (message.EventType == EventTypes.Stock && message.Data.Sequence <= stock.Sequence)
                throw new PermanentFailureException("Stale or conflicting stock sequence requires reconciliation.");
            var entries = await db.Ledger.Where(x => x.BookId == book.Id && x.DistributorId == distributor).ToListAsync(token);
            if (entries.Any(x => x.Sequence == message.Data.Sequence))
                throw new PermanentFailureException("Conflicting inventory sequence.");
            var entry = new InventoryLedgerEntry
            {
                EventId = message.Id, BookId = book.Id, DistributorId = distributor,
                EventType = message.EventType, Quantity = quantity, Sequence = message.Data.Sequence, RecordedAt = message.EventTime
            };
            db.Ledger.Add(entry);
            entries.Add(entry);
            var available = 0;
            string? alert = null;
            foreach (var change in entries.OrderBy(x => x.Sequence))
            {
                try { available = InventoryRules.AvailableAfter(change.EventType, available, change.Quantity); }
                catch (PermanentFailureException) when (change.EventType is EventTypes.Sold or EventTypes.Distributed)
                {
                    throw new ConflictException("Inventory debit precedes its credit; retry or reconcile missing events.");
                }
                if (change.EventType is EventTypes.LowStock or EventTypes.Depleted)
                    alert = change.EventType;
                if (change.EventType is EventTypes.Delivered or EventTypes.Stock && available > options.Value.LowStockThreshold)
                    alert = null;
            }
            stock.AvailableQuantity = available;
            stock.PrintedQuantity = entries.Where(x => x.EventType == EventTypes.Printed).Sum(x => x.Quantity);
            stock.DeliveredQuantity = entries.Where(x => x.EventType == EventTypes.Delivered).Sum(x => x.Quantity);
            stock.DistributedQuantity = entries.Where(x => x.EventType == EventTypes.Distributed).Sum(x => x.Quantity);
            stock.SoldQuantity = entries.Where(x => x.EventType == EventTypes.Sold).Sum(x => x.Quantity);
            stock.Alert = alert;
            stock.Sequence = entries.Max(x => x.Sequence);
            stock.UpdatedAt = entries.Max(x => x.RecordedAt);
            if (message.Data.PrintOrderId is { } orderId)
            {
                var order = await db.PrintOrders.SingleOrDefaultAsync(x => x.Id == orderId && x.BookId == book.Id &&
                    x.DistributorId == distributor, token) ?? throw new PermanentFailureException("Order callback does not match stock.");
                var next = message.EventType switch
                {
                    EventTypes.Printed => PrintOrderStatus.Printed,
                    EventTypes.Delivered => PrintOrderStatus.Delivered,
                    _ => order.Status
                };
                if (next > order.Status)
                    order.Status = next;
            }
        }, ct);
    }
}
