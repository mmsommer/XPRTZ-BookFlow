using System.Net;
using System.Text.Json;
using BookFlow.Core;
using BookFlow.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Protected;

namespace BookFlow.ApiFrontend.Tests;

/// <summary>Tests frontend behavior.</summary>
public sealed class FrontendTests
{
    /// <summary>Verifies callback is durably accepted once.</summary>
    [Fact]
    public async Task Callback_is_durably_accepted_once()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var service = new CallbackService(test.Db, new ConfigurationBuilder().Build());
        var message = test.InventoryEvent(EventTypes.Stock, book, 1, 50);
        await service.AcceptAsync(test.Distributor, message, CancellationToken.None);
        await service.AcceptAsync(test.Distributor, message, CancellationToken.None);
        test.Db.Outbox.Should().ContainSingle();
        test.Db.Idempotency.Should().ContainSingle();
    }

    /// <summary>Verifies distributor cannot report another distributors stock.</summary>
    [Fact]
    public async Task Distributor_cannot_report_another_distributors_stock()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var service = new CallbackService(test.Db, new ConfigurationBuilder().Build());
        await FluentActions.Awaiting(() => service.AcceptAsync(test.Distributor,
            test.InventoryEvent(EventTypes.Stock, book, 1, 50) with
            { Data = new EventData(test.TenantId, 1, BookId: book.Id, DistributorId: "other", Quantity: 50) }, CancellationToken.None))
            .Should().ThrowAsync<AccessDeniedException>();
        test.Db.Outbox.Should().BeEmpty();
    }

    /// <summary>Verifies reusing callback id with different quantity is a conflict.</summary>
    [Fact]
    public async Task Reusing_callback_id_with_different_quantity_is_a_conflict()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var service = new CallbackService(test.Db, new ConfigurationBuilder().Build());
        var message = test.InventoryEvent(EventTypes.Stock, book, 1, 50);
        await service.AcceptAsync(test.Distributor, message, CancellationToken.None);
        await FluentActions.Awaiting(() => service.AcceptAsync(test.Distributor,
            message with { Data = message.Data with { Quantity = 20 } }, CancellationToken.None))
            .Should().ThrowAsync<ConflictException>();
    }

    /// <summary>Verifies print provider callbacks cannot report more copies than were ordered.</summary>
    [Fact]
    public async Task Print_provider_cannot_overreport_order_quantity()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var order = new PrintOrder
        {
            Id = Guid.NewGuid(), BookId = book.Id, DistributorId = test.Distributor.Id,
            Quantity = 10, Status = PrintOrderStatus.Requested
        };
        test.Db.PrintOrders.Add(order);
        await test.Db.SaveChangesAsync();
        var provider = new Actor("print-provider", test.TenantId, "PrintProvider");
        var service = new CallbackService(test.Db, new ConfigurationBuilder().Build());
        var message = DomainEvent.Create(EventTypes.Delivered,
            new EventData(test.TenantId, 4, BookId: book.Id, DistributorId: test.Distributor.Id,
                PrintOrderId: order.Id, Quantity: 11));

        await FluentActions.Awaiting(() => service.AcceptAsync(provider, message, CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();

        order.Quantity.Should().Be(10);
        order.Status.Should().Be(PrintOrderStatus.Requested);
        test.Db.Outbox.Should().BeEmpty();
    }

    /// <summary>Verifies print order callbacks follow fulfillment state and accept matching retries.</summary>
    [Fact]
    public async Task Print_order_callbacks_follow_fulfillment_state()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var order = new PrintOrder
        {
            Id = Guid.NewGuid(), BookId = book.Id, DistributorId = test.Distributor.Id,
            Quantity = 10, Status = PrintOrderStatus.Requested
        };
        test.Db.PrintOrders.Add(order);
        await test.Db.SaveChangesAsync();
        var provider = new Actor("print-provider", test.TenantId, "PrintProvider");
        var service = new CallbackService(test.Db, new ConfigurationBuilder().Build());
        var printed = DomainEvent.Create(EventTypes.Printed,
            new EventData(test.TenantId, 4, BookId: book.Id, DistributorId: test.Distributor.Id,
                PrintOrderId: order.Id, Quantity: 10));

        await service.AcceptAsync(provider, printed, CancellationToken.None);
        order.Status.Should().Be(PrintOrderStatus.Printed);
        await service.AcceptAsync(provider, printed, CancellationToken.None);
        var repeatedPrint = printed with { Id = Guid.NewGuid().ToString() };
        await FluentActions.Awaiting(() => service.AcceptAsync(provider, repeatedPrint, CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();

        var delivered = DomainEvent.Create(EventTypes.Delivered,
            new EventData(test.TenantId, 5, BookId: book.Id, DistributorId: test.Distributor.Id,
                PrintOrderId: order.Id, Quantity: 10));
        await service.AcceptAsync(provider, delivered, CancellationToken.None);

        order.Status.Should().Be(PrintOrderStatus.Delivered);
        test.Db.Outbox.Should().HaveCount(2);
    }

    /// <summary>Verifies forwarding reports dependency failure instead of success.</summary>
    [Fact]
    public async Task Forwarding_reports_dependency_failure_instead_of_success()
    {
        var http = new Mock<HttpMessageHandler>();
        http.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(http.Object) { BaseAddress = new Uri("http://notifications.test/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("notifications")).Returns(client);
        var service = new FrontendService(factory.Object);
        var message = DomainEvent.Create(EventTypes.Assigned, new EventData(Guid.NewGuid(), 1, ManuscriptId: Guid.NewGuid()));
        var json = JsonSerializer.SerializeToElement(message, EventJson.Options);
        await FluentActions.Awaiting(() => service.ForwardNotificationsAsync([json], "Bearer test-token", CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();
        factory.Verify(x => x.CreateClient("notifications"), Times.Once);
    }
}
