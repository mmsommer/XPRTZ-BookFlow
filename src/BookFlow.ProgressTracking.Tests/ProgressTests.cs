using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Testing;
using FluentAssertions;
using Moq;

namespace BookFlow.ProgressTracking.Tests;

/// <summary>Tests progress behavior.</summary>
public sealed class ProgressTests
{
    /// <summary>Verifies lifecycle events map to the expected manuscript status.</summary>
    /// <param name="type">The domain event type.</param>
    /// <param name="expected">The expected manuscript status.</param>
    [Theory]
    [InlineData(EventTypes.Uploaded, ManuscriptStatus.Submitted)]
    [InlineData(EventTypes.Assigned, ManuscriptStatus.AssignedForEditing)]
    [InlineData(EventTypes.Edited, ManuscriptStatus.AwaitingAuthorResponse)]
    [InlineData(EventTypes.Revised, ManuscriptStatus.AwaitingEditorReview)]
    [InlineData(EventTypes.Approved, ManuscriptStatus.Approved)]
    public void Maps_lifecycle_event(string type, ManuscriptStatus expected) => ProgressRules.Map(type).Should().Be(expected);

    /// <summary>Verifies out-of-order history is buffered until contiguous transitions can be projected.</summary>
    [Fact]
    public async Task Buffers_out_of_order_history_and_projects_only_contiguous_transitions()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript(ManuscriptStatus.AwaitingAuthorResponse, 3);
        var handler = new ProgressHandler(test.Db, new EventTransaction(test.Db));
        var edited = test.Event(EventTypes.Edited, manuscript, 3);
        await handler.HandleAsync(edited, CancellationToken.None);
        manuscript.ProgressSequence.Should().Be(0);
        await handler.HandleAsync(test.Event(EventTypes.Uploaded, manuscript, 1), CancellationToken.None);
        await handler.HandleAsync(test.Event(EventTypes.Assigned, manuscript, 2), CancellationToken.None);
        await handler.HandleAsync(edited, CancellationToken.None);

        manuscript.ProgressSequence.Should().Be(3);
        manuscript.ProgressStatus.Should().Be(ManuscriptStatus.AwaitingAuthorResponse);
        test.Db.History.Should().HaveCount(3);
    }

    /// <summary>Verifies unsupported events are rejected before opening a transaction.</summary>
    [Fact]
    public async Task Rejects_unsupported_event_before_transaction()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var transaction = new Mock<IEventTransaction>(MockBehavior.Strict);
        var handler = new ProgressHandler(test.Db, transaction.Object);
        await FluentActions.Awaiting(() => handler.HandleAsync(test.Event(EventTypes.Proofprint, manuscript), CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
        transaction.VerifyNoOtherCalls();
    }
}
