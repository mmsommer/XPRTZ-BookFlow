using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Testing;
using FluentAssertions;
using Moq;

namespace BookFlow.WebApp.Tests;

public sealed class ManuscriptTests
{
    [Fact]
    public async Task Upload_retry_preserves_one_version_and_durable_event()
    {
        using var test = new TestDatabase();
        var blobs = new Mock<IBlobStore>();
        var service = new ManuscriptService(test.Db, blobs.Object);
        var request = new CreateManuscriptRequest("My book", "publisher", "upload-1", "aGVsbG8=");

        var first = await service.CreateAsync(test.Author, request, CancellationToken.None);
        var replay = await service.CreateAsync(test.Author, request, CancellationToken.None);

        replay.Id.Should().Be(first.Id);
        test.Db.Manuscripts.Should().ContainSingle();
        test.Db.Versions.Should().ContainSingle();
        test.Db.Outbox.Should().ContainSingle();
        test.Db.Uploads.Should().ContainSingle().Which.State.Should().Be("Committed");
        blobs.Verify(x => x.PutAsync("manuscripts", It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reusing_key_with_different_payload_returns_conflict()
    {
        using var test = new TestDatabase();
        var service = new ManuscriptService(test.Db, new Mock<IBlobStore>().Object);
        var request = new CreateManuscriptRequest("My book", "publisher", "upload-1", "aGVsbG8=");
        await service.CreateAsync(test.Author, request, CancellationToken.None);
        await FluentActions.Awaiting(() => service.CreateAsync(test.Author, request with { Title = "Changed" }, CancellationToken.None))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Failed_blob_upload_leaves_recoverable_reservation_but_no_domain_event()
    {
        using var test = new TestDatabase();
        var blobs = new Mock<IBlobStore>();
        blobs.Setup(x => x.PutAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Storage unavailable"));
        var service = new ManuscriptService(test.Db, blobs.Object);
        await FluentActions.Awaiting(() => service.CreateAsync(test.Author,
            new CreateManuscriptRequest("Title", "publisher", "retry", "aGVsbG8="), CancellationToken.None))
            .Should().ThrowAsync<IOException>();
        test.Db.Manuscripts.Should().BeEmpty();
        test.Db.Outbox.Should().BeEmpty();
        test.Db.Uploads.Should().ContainSingle().Which.State.Should().Be("Pending");
    }

    [Fact]
    public async Task Foreign_author_cannot_read_manuscript_or_download_file()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var blobs = new Mock<IBlobStore>(MockBehavior.Strict);
        var service = new ManuscriptService(test.Db, blobs.Object);
        var stranger = new Actor("other", test.TenantId, "Author");
        await FluentActions.Awaiting(() => service.GetAsync(stranger, manuscript.Id, CancellationToken.None))
            .Should().ThrowAsync<AccessDeniedException>();
        await FluentActions.Awaiting(() => service.FileAsync(stranger, manuscript.Id, test.Db.Versions.Single().Id, false, CancellationToken.None))
            .Should().ThrowAsync<AccessDeniedException>();
        blobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Book_list_includes_books_beyond_the_manuscript_list_limit()
    {
        using var test = new TestDatabase();
        var manuscripts = Enumerable.Range(0, 105).Select(_ => new Manuscript
        {
            Id = Guid.NewGuid(), TenantId = test.TenantId, AuthorId = test.Author.Id,
            PublisherId = test.Publisher.Id, Title = "Test manuscript",
            Status = ManuscriptStatus.Approved, SubmittedAt = DateTimeOffset.UtcNow
        }).ToArray();
        test.Db.Manuscripts.AddRange(manuscripts);
        test.Db.Books.AddRange(manuscripts.Select((manuscript, index) => new Book
        {
            Id = Guid.NewGuid(), ManuscriptId = manuscript.Id, TenantId = test.TenantId,
            PublisherId = test.Publisher.Id, Title = $"Book {index}", Isbn = $"isbn-{index}", Orderable = true
        }));
        await test.Db.SaveChangesAsync();

        var service = new ManuscriptService(test.Db, new Mock<IBlobStore>().Object);

        (await service.ListAsync(test.Author, CancellationToken.None)).Should().HaveCount(100);
        (await service.BooksAsync(test.Author, CancellationToken.None)).Should().HaveCount(105);
    }

    [Fact]
    public async Task Version_list_includes_related_details_for_each_version()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var first = test.Db.Versions.Single();
        var second = new ManuscriptVersion
        {
            Id = Guid.NewGuid(), ManuscriptId = manuscript.Id, Number = 2,
            ParentId = first.Id, BlobName = "second.bin", CreatedAt = DateTimeOffset.UtcNow
        };
        var firstHighlight = new ManuscriptHighlight
        {
            Id = Guid.NewGuid(), VersionId = first.Id, StartOffset = 0, EndOffset = 5,
            OriginalText = "first", EditedText = "edit1"
        };
        var secondHighlight = new ManuscriptHighlight
        {
            Id = Guid.NewGuid(), VersionId = second.Id, StartOffset = 6, EndOffset = 11,
            OriginalText = "second", EditedText = "edit2"
        };
        var firstFeedback = new AuthorFeedback
        {
            Id = Guid.NewGuid(), VersionId = first.Id, AuthorId = test.Author.Id,
            Comment = "feedback 1", SubmittedAt = DateTimeOffset.UtcNow
        };
        var secondFeedback = new AuthorFeedback
        {
            Id = Guid.NewGuid(), VersionId = second.Id, AuthorId = test.Author.Id,
            Comment = "feedback 2", SubmittedAt = DateTimeOffset.UtcNow
        };
        var proofprint = new Proofprint
        {
            Id = Guid.NewGuid(), VersionId = second.Id, BlobName = "proofprints/second.pdf",
            CreatedAt = DateTimeOffset.UtcNow
        };
        test.Db.Versions.Add(second);
        test.Db.Highlights.AddRange(firstHighlight, secondHighlight);
        test.Db.Feedback.AddRange(firstFeedback, secondFeedback);
        test.Db.Proofprints.Add(proofprint);
        await test.Db.SaveChangesAsync();

        var service = new ManuscriptService(test.Db, new Mock<IBlobStore>().Object);

        var versions = await service.VersionsAsync(test.Author, manuscript.Id, CancellationToken.None);

        versions.Should().HaveCount(2);
        versions[0].Id.Should().Be(first.Id);
        versions[0].Highlights.Should().ContainSingle()
            .Which.OriginalText.Should().Be("first");
        versions[0].Feedback.Should().ContainSingle().Which.Should().Be("feedback 1");
        versions[0].ProofprintId.Should().BeNull();
        versions[1].Id.Should().Be(second.Id);
        versions[1].Highlights.Should().ContainSingle()
            .Which.OriginalText.Should().Be("second");
        versions[1].Feedback.Should().ContainSingle().Which.Should().Be("feedback 2");
        versions[1].ProofprintId.Should().Be(proofprint.Id);
    }

    [Fact]
    public async Task Approval_requires_assigned_editor_and_correct_state()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript(ManuscriptStatus.AwaitingAuthorResponse, 3);
        var service = new ManuscriptService(test.Db, new Mock<IBlobStore>().Object);
        await FluentActions.Awaiting(() => service.ApproveAsync(test.Editor, manuscript.Id,
            new ApproveManuscriptRequest("9780306406157", "distributor", 100, "approve"), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>();
        test.Db.Books.Should().BeEmpty();
        test.Db.PrintOrders.Should().BeEmpty();
    }

    [Theory]
    [InlineData("9780306406158")]
    [InlineData("bad")]
    public void Invalid_isbn_is_rejected(string isbn) =>
        FluentActions.Invoking(() => ManuscriptService.ValidateIsbn(isbn)).Should().Throw<ArgumentException>();
}
