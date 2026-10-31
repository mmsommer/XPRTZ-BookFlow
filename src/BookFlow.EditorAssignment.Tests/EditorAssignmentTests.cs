using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Testing;
using FluentAssertions;
using Moq;

namespace BookFlow.EditorAssignment.Tests;

public sealed class EditorAssignmentTests
{
    [Fact]
    public async Task Assigns_once_and_emits_one_outbox_event_on_redelivery()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var policy = new Mock<IEditorSelectionPolicy>(MockBehavior.Strict);
        policy.Setup(x => x.SelectAsync(test.TenantId, It.IsAny<CancellationToken>())).ReturnsAsync("editor");
        var handler = new EditorAssignmentHandler(test.Db, new EventTransaction(test.Db), policy.Object);
        var message = test.Event(EventTypes.Uploaded, manuscript);

        await handler.HandleAsync(message, CancellationToken.None);
        await handler.HandleAsync(message, CancellationToken.None);

        manuscript.EditorId.Should().Be("editor");
        manuscript.Status.Should().Be(ManuscriptStatus.AssignedForEditing);
        manuscript.Sequence.Should().Be(2);
        test.Db.Outbox.Should().ContainSingle();
        test.Db.Inbox.Should().ContainSingle();
        policy.Verify(x => x.SelectAsync(test.TenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Chooses_least_loaded_active_editor_with_deterministic_tie_breaking()
    {
        using var test = new TestDatabase();
        test.SeedManuscript(ManuscriptStatus.AssignedForEditing);
        test.Db.Participants.Add(new Participant { TenantId = test.TenantId, Id = "another", Role = "Editor", Active = true });
        test.Db.Participants.Add(new Participant { TenantId = Guid.NewGuid(), Id = "foreign", Role = "Editor", Active = true });
        await test.Db.SaveChangesAsync();

        (await new LeastLoadedEditorPolicy(test.Db).SelectAsync(test.TenantId, CancellationToken.None)).Should().Be("another");
    }

    [Fact]
    public async Task No_capacity_does_not_acknowledge_or_publish()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var policy = new Mock<IEditorSelectionPolicy>();
        policy.Setup(x => x.SelectAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PermanentFailureException("No capacity"));
        var handler = new EditorAssignmentHandler(test.Db, new EventTransaction(test.Db), policy.Object);

        await FluentActions.Awaiting(() => handler.HandleAsync(test.Event(EventTypes.Uploaded, manuscript), CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
        test.Db.Inbox.Should().BeEmpty();
        test.Db.Outbox.Should().BeEmpty();
    }
}
