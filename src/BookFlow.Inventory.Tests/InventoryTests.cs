using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Testing;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Options;

namespace BookFlow.Inventory.Tests;

public sealed class InventoryTests
{
    [Fact]
    public async Task Absolute_report_and_delta_redelivery_do_not_double_subtract()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var handler = new InventoryHandler(test.Db, new EventTransaction(test.Db), Options.Create(new InventoryOptions()));
        await handler.HandleAsync(test.InventoryEvent(EventTypes.Stock, book, 1, 100), CancellationToken.None);
        var sold = test.InventoryEvent(EventTypes.Sold, book, 2, 20);
        await handler.HandleAsync(sold, CancellationToken.None);
        await handler.HandleAsync(sold, CancellationToken.None);
        await handler.HandleAsync(test.InventoryEvent(EventTypes.Stock, book, 3, 75), CancellationToken.None);

        test.Db.Stock.Should().ContainSingle().Which.AvailableQuantity.Should().Be(75);
        test.Db.Stock.Single().SoldQuantity.Should().Be(20);
        test.Db.Ledger.Should().HaveCount(3);
    }

    [Fact]
    public async Task Out_of_order_credits_are_reconciled_and_do_not_regress_sequence()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var handler = new InventoryHandler(test.Db, new EventTransaction(test.Db), Options.Create(new InventoryOptions()));
        await handler.HandleAsync(test.InventoryEvent(EventTypes.Delivered, book, 2, 50), CancellationToken.None);
        await handler.HandleAsync(test.InventoryEvent(EventTypes.Printed, book, 1, 50), CancellationToken.None);

        var stock = test.Db.Stock.Single();
        stock.Sequence.Should().Be(2);
        stock.AvailableQuantity.Should().Be(50);
        stock.PrintedQuantity.Should().Be(50);
        stock.DeliveredQuantity.Should().Be(50);
    }

    [Fact]
    public async Task Late_event_rebuilds_stock_in_sequence_order()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var handler = new InventoryHandler(test.Db, new EventTransaction(test.Db), Options.Create(new InventoryOptions()));
        await handler.HandleAsync(test.InventoryEvent(EventTypes.Stock, book, 1, 100), CancellationToken.None);
        await handler.HandleAsync(test.InventoryEvent(EventTypes.Stock, book, 3, 80), CancellationToken.None);

        await handler.HandleAsync(test.InventoryEvent(EventTypes.Delivered, book, 2, 50), CancellationToken.None);

        var stock = test.Db.Stock.Single();
        stock.AvailableQuantity.Should().Be(80);
        stock.DeliveredQuantity.Should().Be(50);
        stock.Sequence.Should().Be(3);
    }

    [Fact]
    public async Task Alerts_do_not_modify_available_quantity()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var handler = new InventoryHandler(test.Db, new EventTransaction(test.Db), Options.Create(new InventoryOptions()));
        await handler.HandleAsync(test.InventoryEvent(EventTypes.Stock, book, 1, 10), CancellationToken.None);
        await handler.HandleAsync(test.InventoryEvent(EventTypes.LowStock, book, 2, 10), CancellationToken.None);
        test.Db.Stock.Single().AvailableQuantity.Should().Be(10);
        test.Db.Stock.Single().Alert.Should().Be(EventTypes.LowStock);
    }

    [Fact]
    public async Task Rejects_unrouted_event_without_running_transaction()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var transaction = new Mock<IEventTransaction>(MockBehavior.Strict);
        var handler = new InventoryHandler(test.Db, transaction.Object, Options.Create(new InventoryOptions()));
        await FluentActions.Awaiting(() => handler.HandleAsync(test.InventoryEvent(EventTypes.Order, book, 1, 10), CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
        transaction.VerifyNoOtherCalls();
    }

    [Fact]
    public void Debit_cannot_make_inventory_negative() =>
        FluentActions.Invoking(() => InventoryRules.AvailableAfter(EventTypes.Sold, 10, 11))
            .Should().Throw<PermanentFailureException>();

    [Fact]
    public async Task Low_stock_threshold_is_configurable_and_exact()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var handler = new InventoryHandler(test.Db, new EventTransaction(test.Db),
            Options.Create(new InventoryOptions { LowStockThreshold = 25 }));
        await handler.HandleAsync(test.InventoryEvent(EventTypes.Stock, book, 1, 25), CancellationToken.None);
        await handler.HandleAsync(test.InventoryEvent(EventTypes.LowStock, book, 2, 25), CancellationToken.None);
        test.Db.Stock.Single().Alert.Should().Be(EventTypes.LowStock);
        test.Db.Stock.Single().AvailableQuantity.Should().Be(25);
        await FluentActions.Awaiting(() => handler.HandleAsync(
            test.InventoryEvent(EventTypes.LowStock, book, 3, 10), CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
    }
}
