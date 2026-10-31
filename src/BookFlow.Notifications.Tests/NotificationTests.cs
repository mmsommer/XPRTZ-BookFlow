using System.Text.Json;
using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BookFlow.Notifications.Tests;

public sealed class NotificationTests
{
    [Fact]
    public async Task Approval_schedules_author_email_and_publisher_api_once()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var message = DomainEvent.Create(EventTypes.Approved, new EventData(test.TenantId, 3, book.ManuscriptId, BookId: book.Id));
        var handler = new NotificationHandler(test.Db, new EventTransaction(test.Db), Configuration());
        await handler.HandleAsync(message, CancellationToken.None);
        await handler.HandleAsync(message, CancellationToken.None);
        var notifications = test.Db.NotificationDeliveries.AsEnumerable()
            .Select(x => JsonSerializer.Deserialize<NotificationMessage>(x.Payload, EventJson.Options)!).ToArray();
        notifications.Should().HaveCount(2);
        notifications.Should().Contain(x => x.Destination == "author@example.test" && x.Template == "manuscript-approved");
        notifications.Should().Contain(x => x.PublisherApi && x.Destination == "publisher" && x.Template == "book-orderable");
    }

    [Fact]
    public async Task Accepts_webhook_durably_before_recipient_resolution()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var message = test.Event(EventTypes.Proofprint, manuscript);
        message = message with { Data = message.Data with { ProofprintId = Guid.NewGuid() } };
        var service = new NotificationAcceptance(test.Db);
        await service.AcceptAsync([message, message], CancellationToken.None);
        await service.AcceptAsync([message], CancellationToken.None);
        test.Db.LocalDeliveries.Should().ContainSingle().Which.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task Malformed_notification_is_rejected_before_acknowledgement()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var service = new NotificationAcceptance(test.Db);
        await FluentActions.Awaiting(() => service.AcceptAsync([test.Event(EventTypes.Proofprint, manuscript)], CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
        test.Db.LocalDeliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_failure_is_persisted_and_not_marked_sent()
    {
        using var test = new TestDatabase();
        var message = new NotificationMessage("author@example.test", "manuscript-edited", "https://bookflow.test/");
        test.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            Key = "notification-key", Payload = JsonSerializer.Serialize(message, EventJson.Options)
        });
        await test.Db.SaveChangesAsync();
        var provider = new Mock<INotificationProvider>();
        provider.Setup(x => x.SendAsync(message, "notification-key", It.IsAny<CancellationToken>()))
            .Callback(() => test.Db.Database.CurrentTransaction.Should().BeNull())
            .ThrowsAsync(new HttpRequestException("Unavailable"));
        var service = new NotificationDeliveryService(test.Db, provider.Object, NullLogger<NotificationDeliveryService>.Instance);
        await service.SendNextAsync(CancellationToken.None);
        var delivery = test.Db.NotificationDeliveries.Single();
        delivery.Sent.Should().BeFalse();
        delivery.Attempts.Should().Be(1);
        delivery.FailureCode.Should().Be(nameof(HttpRequestException));
        delivery.NextAttemptAt.Should().NotBeNull();
        delivery.NextAttemptAtUtcTicks.Should().Be(delivery.NextAttemptAt?.UtcTicks);
        delivery.LeaseId.Should().BeNull();
        delivery.LeaseUntilUtcTicks.Should().Be(0);
        provider.Verify(x => x.SendAsync(message, "notification-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sender_selects_due_delivery_after_future_retry_rows()
    {
        using var test = new TestDatabase();
        var future = DateTimeOffset.UtcNow.AddHours(1);
        for (var i = 0; i < 100; i++)
        {
            test.Db.NotificationDeliveries.Add(new NotificationDelivery
            {
                Key = $"future-{i:D3}",
                Payload = JsonSerializer.Serialize(new NotificationMessage("future@example.test", "template", "https://bookflow.test/"), EventJson.Options),
                NextAttemptAt = future,
                NextAttemptAtUtcTicks = future.UtcTicks
            });
        }
        var dueMessage = new NotificationMessage("due@example.test", "template", "https://bookflow.test/");
        test.Db.NotificationDeliveries.Add(new NotificationDelivery
        {
            Key = "zz-due",
            Payload = JsonSerializer.Serialize(dueMessage, EventJson.Options)
        });
        await test.Db.SaveChangesAsync();

        var provider = new Mock<INotificationProvider>();
        provider.Setup(x => x.SendAsync(dueMessage, "zz-due", It.IsAny<CancellationToken>()))
            .Callback(() => test.Db.Database.CurrentTransaction.Should().BeNull())
            .Returns(Task.CompletedTask);
        var service = new NotificationDeliveryService(test.Db, provider.Object,
            NullLogger<NotificationDeliveryService>.Instance);
        await service.SendNextAsync(CancellationToken.None);

        provider.Verify(x => x.SendAsync(dueMessage, "zz-due", It.IsAny<CancellationToken>()), Times.Once);
        var delivery = test.Db.NotificationDeliveries.Single(x => x.Key == "zz-due");
        delivery.Sent.Should().BeTrue();
        delivery.LeaseId.Should().BeNull();
    }

    [Fact]
    public async Task Rejects_unapproved_listing_host()
    {
        using var test = new TestDatabase();
        var book = test.SeedBook();
        var handler = new NotificationHandler(test.Db, new EventTransaction(test.Db), Configuration());
        var message = DomainEvent.Create(EventTypes.Available,
            new EventData(test.TenantId, 1, BookId: book.Id, ListingUrl: "https://untrusted.test/"));
        await FluentActions.Awaiting(() => handler.HandleAsync(message, CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
        test.Db.NotificationDeliveries.Should().BeEmpty();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BookFlow:PublicUrl"] = "https://bookflow.test",
            ["BookFlow:ListingHosts:0"] = "shop.example.test"
        }).Build();
}
