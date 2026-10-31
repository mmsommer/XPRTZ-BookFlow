using System.Net;
using System.Text.Json;
using BookFlow.Core;
using BookFlow.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Protected;

namespace BookFlow.ApiFrontend.Tests;

public sealed class FrontendTests
{
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
