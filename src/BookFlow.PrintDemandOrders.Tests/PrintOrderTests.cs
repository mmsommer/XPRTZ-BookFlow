using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Testing;
using FluentAssertions;
using Moq;

namespace BookFlow.PrintDemandOrders.Tests;

/// <summary>Tests print order behavior.</summary>
public sealed class PrintOrderTests
{
    /// <summary>Verifies print events submit once with a stable key without creating stock.</summary>
    /// <param name="type">The domain event type.</param>
    [Theory]
    [InlineData(EventTypes.Approved)]
    [InlineData(EventTypes.Order)]
    public async Task Submits_once_with_stable_key_and_does_not_create_stock(string type)
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var order = new PrintOrder { Id = Guid.NewGuid(), BookId = book.Id, DistributorId = "distributor", Quantity = 100 };
        test.Db.PrintOrders.Add(order);
        await test.Db.SaveChangesAsync();
        var provider = new Mock<IPrintProvider>(MockBehavior.Strict);
        provider.Setup(x => x.OrderAsync(It.Is<PrintSubmission>(s => s.BookId == book.Id && s.Quantity == 100),
            $"print-order:{order.Id}", It.IsAny<CancellationToken>()))
            .Callback(() => test.Db.Database.CurrentTransaction.Should().BeNull())
            .ReturnsAsync("external-123");
        var handler = new PrintOrderHandler(test.Db, new EventTransaction(test.Db), provider.Object);
        var message = DomainEvent.Create(type, new EventData(test.TenantId, 1, BookId: book.Id, PrintOrderId: order.Id));

        await handler.HandleAsync(message, CancellationToken.None);
        await handler.HandleAsync(message, CancellationToken.None);

        order.ExternalReference.Should().Be("external-123");
        order.Status.Should().Be(PrintOrderStatus.Submitted);
        test.Db.Stock.Should().BeEmpty();
        provider.Verify(x => x.OrderAsync(It.IsAny<PrintSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies invalid quantity does not contact provider.</summary>
    [Fact]
    public async Task Invalid_quantity_does_not_contact_provider()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var order = new PrintOrder { Id = Guid.NewGuid(), BookId = book.Id, DistributorId = "distributor", Quantity = 0 };
        test.Db.PrintOrders.Add(order);
        await test.Db.SaveChangesAsync();
        var provider = new Mock<IPrintProvider>(MockBehavior.Strict);
        var handler = new PrintOrderHandler(test.Db, new EventTransaction(test.Db), provider.Object);

        await FluentActions.Awaiting(() => handler.HandleAsync(DomainEvent.Create(EventTypes.Order,
            new EventData(test.TenantId, 1, BookId: book.Id, PrintOrderId: order.Id)), CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
        provider.VerifyNoOtherCalls();
    }
}
