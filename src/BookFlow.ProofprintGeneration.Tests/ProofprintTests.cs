using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Testing;
using FluentAssertions;
using Moq;

namespace BookFlow.ProofprintGeneration.Tests;

public sealed class ProofprintTests
{
    [Fact]
    public async Task Stores_pdf_before_publishing_and_deduplicates_by_version()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var version = test.Db.Versions.Single();
        var blobs = new Mock<IBlobStore>(MockBehavior.Strict);
        var provider = new Mock<IPrintProvider>(MockBehavior.Strict);
        byte[] source = [1, 2, 3];
        var pdf = "%PDF-1.4\nsynthetic"u8.ToArray();
        blobs.Setup(x => x.GetAsync("manuscripts", version.BlobName, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        provider.Setup(x => x.GenerateProofprintAsync(source, $"proofprint:{version.Id}", It.IsAny<CancellationToken>())).ReturnsAsync(pdf);
        blobs.Setup(x => x.PutAsync("proofprints", It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback(() => test.Db.Outbox.Should().BeEmpty()).Returns(Task.CompletedTask);
        var handler = new ProofprintHandler(test.Db, new EventTransaction(test.Db), blobs.Object, provider.Object);
        var message = test.Event(EventTypes.Uploaded, manuscript);

        await handler.HandleAsync(message, CancellationToken.None);
        await handler.HandleAsync(message, CancellationToken.None);
        await handler.HandleAsync(message with { Id = Guid.NewGuid().ToString("N") }, CancellationToken.None);

        test.Db.Proofprints.Should().ContainSingle().Which.VersionId.Should().Be(version.Id);
        test.Db.Outbox.Should().ContainSingle();
        provider.Verify(x => x.GenerateProofprintAsync(source, $"proofprint:{version.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Invalid_pdf_does_not_create_link_or_event()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var blobs = new Mock<IBlobStore>();
        blobs.Setup(x => x.GetAsync("manuscripts", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([1]);
        var provider = new Mock<IPrintProvider>();
        provider.Setup(x => x.GenerateProofprintAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);
        var handler = new ProofprintHandler(test.Db, new EventTransaction(test.Db), blobs.Object, provider.Object);

        await FluentActions.Awaiting(() => handler.HandleAsync(test.Event(EventTypes.Uploaded, manuscript), CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
        test.Db.Proofprints.Should().BeEmpty();
        test.Db.Outbox.Should().BeEmpty();
        blobs.Verify(x => x.PutAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
