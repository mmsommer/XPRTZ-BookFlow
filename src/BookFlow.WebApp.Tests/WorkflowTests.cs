using System.Text.Json;
using BookFlow.Core;
using BookFlow.EditorAssignment;
using BookFlow.Infrastructure;
using BookFlow.Inventory;
using BookFlow.Notifications;
using BookFlow.PrintDemandOrders;
using BookFlow.ProgressTracking;
using BookFlow.ProofprintGeneration;
using BookFlow.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Microsoft.Extensions.Options;

namespace BookFlow.WebApp.Tests;

/// <summary>Tests workflow behavior.</summary>
public sealed class WorkflowTests
{
    /// <summary>Exercises upload, editing, revision, approval, printing, delivery, sale, and reprint end to end.</summary>
    [Fact]
    public async Task Upload_edit_revise_approve_print_deliver_sell_and_reprint_workflow()
    {
        using var test = new TestDatabase();
        var stored = new Dictionary<string, byte[]>();
        var blobs = new Mock<IBlobStore>();
        blobs.Setup(x => x.PutAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, ReadOnlyMemory<byte>, CancellationToken>((container, name, data, _) => stored[container + "/" + name] = data.ToArray())
            .Returns(Task.CompletedTask);
        blobs.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string container, string name, CancellationToken _) => stored[container + "/" + name]);
        var provider = new Mock<IPrintProvider>();
        provider.Setup(x => x.GenerateProofprintAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("%PDF-1.4\nsynthetic"u8.ToArray());
        provider.Setup(x => x.OrderAsync(It.IsAny<PrintSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("provider-order");
        var policy = new Mock<IEditorSelectionPolicy>();
        policy.Setup(x => x.SelectAsync(test.TenantId, It.IsAny<CancellationToken>())).ReturnsAsync("editor");
        var transaction = new EventTransaction(test.Db);
        var assignment = new EditorAssignmentHandler(test.Db, transaction, policy.Object);
        var proofs = new ProofprintHandler(test.Db, transaction, blobs.Object, provider.Object);
        var progress = new ProgressHandler(test.Db, transaction);
        var printing = new PrintOrderHandler(test.Db, transaction, provider.Object);
        var inventory = new InventoryHandler(test.Db, transaction, Options.Create(new InventoryOptions()));
        var notifications = new NotificationHandler(test.Db, transaction,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BookFlow:PublicUrl"] = "https://bookflow.example.test",
                ["BookFlow:ListingHosts:0"] = "shop.example.test"
            }).Build());
        var service = new ManuscriptService(test.Db, blobs.Object);

        var manuscript = await service.CreateAsync(test.Author,
            new CreateManuscriptRequest("Title", "publisher", "upload", "aGVsbG8="), CancellationToken.None);
        var uploaded = Latest(EventTypes.Uploaded);
        await proofs.HandleAsync(uploaded, CancellationToken.None);
        await assignment.HandleAsync(uploaded, CancellationToken.None);
        await progress.HandleAsync(uploaded, CancellationToken.None);
        await progress.HandleAsync(Latest(EventTypes.Assigned), CancellationToken.None);
        await notifications.HandleAsync(Latest(EventTypes.Assigned), CancellationToken.None);
        await notifications.HandleAsync(Latest(EventTypes.Proofprint), CancellationToken.None);
        var initial = (await service.VersionsAsync(test.Author, manuscript.Id, CancellationToken.None)).Single();
        var edited = await service.VersionAsync(test.Editor, manuscript.Id,
            new CreateVersionRequest(initial.Id, "edit", "ZWRpdGVk", null, [new HighlightRequest(0, 5, "hello", "edited")]), true, CancellationToken.None);
        await proofs.HandleAsync(Latest(EventTypes.Edited), CancellationToken.None);
        await progress.HandleAsync(Latest(EventTypes.Edited), CancellationToken.None);
        await notifications.HandleAsync(Latest(EventTypes.Edited), CancellationToken.None);
        var revised = await service.VersionAsync(test.Author, manuscript.Id,
            new CreateVersionRequest(edited.Id, "revision", "cmV2aXNlZA==", "Accepted changes", []), false, CancellationToken.None);
        await proofs.HandleAsync(Latest(EventTypes.Revised), CancellationToken.None);
        await progress.HandleAsync(Latest(EventTypes.Revised), CancellationToken.None);
        await notifications.HandleAsync(Latest(EventTypes.Revised), CancellationToken.None);
        var book = await service.ApproveAsync(test.Editor, manuscript.Id,
            new ApproveManuscriptRequest("9780306406157", "distributor", 100, "approve"), CancellationToken.None);
        var approved = Latest(EventTypes.Approved);
        await printing.HandleAsync(approved, CancellationToken.None);
        await printing.HandleAsync(approved, CancellationToken.None);
        await progress.HandleAsync(approved, CancellationToken.None);
        await notifications.HandleAsync(approved, CancellationToken.None);
        await inventory.HandleAsync(test.InventoryEvent(EventTypes.Delivered, test.Db.Books.Single(), 1, 100), CancellationToken.None);
        var sold = test.InventoryEvent(EventTypes.Sold, test.Db.Books.Single(), 2, 90);
        await inventory.HandleAsync(sold, CancellationToken.None);
        await inventory.HandleAsync(sold, CancellationToken.None);
        var low = test.InventoryEvent(EventTypes.LowStock, test.Db.Books.Single(), 3, 10);
        await inventory.HandleAsync(low, CancellationToken.None);
        await notifications.HandleAsync(low, CancellationToken.None);
        var available = test.InventoryEvent(EventTypes.Available, test.Db.Books.Single(), 4, 10);
        available = available with { Data = available.Data with { ListingUrl = "https://shop.example.test/title" } };
        await notifications.HandleAsync(available, CancellationToken.None);
        var reprint = await service.OrderAsync(test.Publisher, new CreatePrintOrderRequest(book.Id, "distributor", 50, "reprint"), CancellationToken.None);
        await printing.HandleAsync(Latest(EventTypes.Order), CancellationToken.None);

        (await service.GetAsync(test.Author, manuscript.Id, CancellationToken.None)).ProgressStatus.Should().Be(ManuscriptStatus.Approved);
        test.Db.History.Should().HaveCount(5);
        test.Db.Versions.Should().HaveCount(3);
        test.Db.Highlights.Should().ContainSingle();
        test.Db.Feedback.Should().ContainSingle().Which.VersionId.Should().Be(revised.Id);
        test.Db.Proofprints.Should().HaveCount(3);
        test.Db.Stock.Single().AvailableQuantity.Should().Be(10);
        test.Db.NotificationDeliveries.Should().HaveCount(8);
        (await service.GetOrderAsync(test.Publisher, reprint.Id, CancellationToken.None)).Status.Should().Be(PrintOrderStatus.Submitted);
        provider.Verify(x => x.OrderAsync(It.IsAny<PrintSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        DomainEvent Latest(string type) => test.Db.Outbox.AsEnumerable()
            .Select(x => JsonSerializer.Deserialize<DomainEvent>(x.Payload, EventJson.Options)!)
            .Last(x => x.EventType == type);
    }
}
