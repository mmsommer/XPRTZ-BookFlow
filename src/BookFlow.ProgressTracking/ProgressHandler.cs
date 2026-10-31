using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.ProgressTracking;

public static class ProgressRules
{
    public static ManuscriptStatus Map(string type) => type switch
    {
        EventTypes.Uploaded => ManuscriptStatus.Submitted,
        EventTypes.Assigned => ManuscriptStatus.AssignedForEditing,
        EventTypes.Edited => ManuscriptStatus.AwaitingAuthorResponse,
        EventTypes.Revised => ManuscriptStatus.AwaitingEditorReview,
        EventTypes.Approved => ManuscriptStatus.Approved,
        _ => throw new PermanentFailureException("Unsupported lifecycle event.")
    };

    public static bool CanTransition(ManuscriptStatus from, ManuscriptStatus to) => (from, to) switch
    {
        (ManuscriptStatus.Submitted, ManuscriptStatus.AssignedForEditing) => true,
        (ManuscriptStatus.AssignedForEditing, ManuscriptStatus.AwaitingAuthorResponse or ManuscriptStatus.Approved) => true,
        (ManuscriptStatus.AwaitingAuthorResponse, ManuscriptStatus.AwaitingEditorReview) => true,
        (ManuscriptStatus.AwaitingEditorReview, ManuscriptStatus.AwaitingAuthorResponse or ManuscriptStatus.Approved) => true,
        _ => false
    };
}

public sealed class ProgressHandler(BookFlowDbContext db, IEventTransaction transaction) : IEventHandler
{
    public string Consumer => "progress-tracking";

    public Task HandleAsync(DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Subscriptions[Consumer]);
        return transaction.ExecuteAsync(Consumer, message, async token =>
        {
            var manuscript = await EventLookup.ManuscriptAsync(db, message, token);
            if (message.Data.Sequence > manuscript.Sequence)
                throw new PermanentFailureException("Lifecycle event is ahead of authoritative state.");
            var status = ProgressRules.Map(message.EventType);
            var entries = await db.History.Where(x => x.ManuscriptId == manuscript.Id).ToListAsync(token);
            if (entries.Any(x => x.Sequence == message.Data.Sequence && x.EventId != message.Id))
                throw new PermanentFailureException("Conflicting lifecycle sequence.");
            var entry = new StatusHistory
            {
                EventId = message.Id, ManuscriptId = manuscript.Id,
                Sequence = message.Data.Sequence, Status = status, ChangedAt = message.EventTime
            };
            db.History.Add(entry);
            entries.Add(entry);
            var sequence = 0L;
            var current = ManuscriptStatus.Submitted;
            foreach (var item in entries.OrderBy(x => x.Sequence))
            {
                if (item.Sequence != sequence + 1)
                    break;
                if (sequence == 0 ? item.Status != ManuscriptStatus.Submitted : !ProgressRules.CanTransition(current, item.Status))
                    throw new PermanentFailureException("Invalid lifecycle transition.");
                current = item.Status;
                sequence = item.Sequence;
            }
            if (sequence >= manuscript.ProgressSequence)
            {
                manuscript.ProgressStatus = current;
                manuscript.ProgressSequence = sequence;
            }
        }, ct);
    }
}
