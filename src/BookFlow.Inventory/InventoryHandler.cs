using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookFlow.Inventory;

/// <summary>Provides inventory calculation rules shared by inventory event processing.</summary>
public static class InventoryRules
{
    /// <summary>Calculates available inventory after applying an event quantity.</summary>
    /// <param name="type">The inventory event type.</param>
    /// <param name="current">The available quantity before the event.</param>
    /// <param name="quantity">The quantity associated with the event.</param>
    /// <returns>The available quantity after the event.</returns>
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

/// <summary>Processes inventory events and updates stock, ledger, and print-order state.</summary>
/// <param name="db">The database context used to read and update inventory data.</param>
/// <param name="transaction">The transaction coordinator for event processing.</param>
/// <param name="options">The configured inventory thresholds.</param>
public sealed class InventoryHandler(BookFlowDbContext db, IEventTransaction transaction, IOptions<InventoryOptions> options) : IEventHandler
{
    /// <summary>Gets the consumer name used to identify inventory event processing.</summary>
    public string Consumer => "inventory";

    /// <summary>Validates and applies an inventory-related domain event.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">A token used to cancel asynchronous database operations.</param>
    /// <returns>A task that completes when the event has been processed.</returns>
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
            if (await db.Ledger.AnyAsync(x => x.BookId == book.Id && x.DistributorId == distributor &&
                x.Sequence == message.Data.Sequence, token))
                throw new PermanentFailureException("Conflicting inventory sequence.");
            var entry = new InventoryLedgerEntry
            {
                EventId = message.Id, BookId = book.Id, DistributorId = distributor,
                EventType = message.EventType, Quantity = quantity, Sequence = message.Data.Sequence, RecordedAt = message.EventTime
            };
            db.Ledger.Add(entry);
            if (message.Data.Sequence > stock.Sequence)
            {
                ApplyChange(stock, entry, options.Value.LowStockThreshold);
                stock.Sequence = entry.Sequence;
            }
            else
            {
                var entries = await db.Ledger.Where(x => x.BookId == book.Id && x.DistributorId == distributor)
                    .ToListAsync(token);
                entries.Add(entry);
                RebuildStock(stock, entries, options.Value.LowStockThreshold);
            }
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

    private static void ApplyChange(StockRecord stock, InventoryLedgerEntry change, int lowStockThreshold)
    {
        try { stock.AvailableQuantity = InventoryRules.AvailableAfter(change.EventType, stock.AvailableQuantity, change.Quantity); }
        catch (PermanentFailureException) when (change.EventType is EventTypes.Sold or EventTypes.Distributed)
        {
            throw new ConflictException("Inventory debit precedes its credit; retry or reconcile missing events.");
        }

        switch (change.EventType)
        {
            case EventTypes.Printed:
                stock.PrintedQuantity = checked(stock.PrintedQuantity + change.Quantity);
                break;
            case EventTypes.Delivered:
                stock.DeliveredQuantity = checked(stock.DeliveredQuantity + change.Quantity);
                break;
            case EventTypes.Distributed:
                stock.DistributedQuantity = checked(stock.DistributedQuantity + change.Quantity);
                break;
            case EventTypes.Sold:
                stock.SoldQuantity = checked(stock.SoldQuantity + change.Quantity);
                break;
            case EventTypes.LowStock:
            case EventTypes.Depleted:
                stock.Alert = change.EventType;
                break;
        }

        if (change.EventType is EventTypes.Delivered or EventTypes.Stock &&
            stock.AvailableQuantity > lowStockThreshold)
            stock.Alert = null;
        if (change.RecordedAt > stock.UpdatedAt)
            stock.UpdatedAt = change.RecordedAt;
    }

    private static void RebuildStock(StockRecord stock, IEnumerable<InventoryLedgerEntry> entries, int lowStockThreshold)
    {
        stock.AvailableQuantity = 0;
        stock.PrintedQuantity = 0;
        stock.DeliveredQuantity = 0;
        stock.DistributedQuantity = 0;
        stock.SoldQuantity = 0;
        stock.Sequence = 0;
        stock.Alert = null;
        stock.UpdatedAt = default;
        foreach (var change in entries.OrderBy(x => x.Sequence))
        {
            ApplyChange(stock, change, lowStockThreshold);
            stock.Sequence = Math.Max(stock.Sequence, change.Sequence);
        }
    }
}
