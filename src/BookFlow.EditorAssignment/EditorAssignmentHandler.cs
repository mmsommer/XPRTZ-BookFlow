using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.EditorAssignment;

public interface IEditorSelectionPolicy
{
    Task<string> SelectAsync(Guid tenantId, CancellationToken ct);
}

public sealed class LeastLoadedEditorPolicy(BookFlowDbContext db) : IEditorSelectionPolicy
{
    public async Task<string> SelectAsync(Guid tenantId, CancellationToken ct)
    {
        var editors = await db.Participants.Where(x => x.TenantId == tenantId && x.Active && x.Role == "Editor")
            .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
        if (editors.Count == 0)
            throw new PermanentFailureException("No active editor is eligible for this publisher tenant.");
        var loads = await db.Manuscripts.Where(x => x.TenantId == tenantId &&
            x.EditorId != null && x.Status != ManuscriptStatus.Approved)
            .GroupBy(x => x.EditorId).Select(x => new { Editor = x.Key!, Count = x.Count() }).ToListAsync(ct);
        return editors.OrderBy(x => loads.SingleOrDefault(y => y.Editor == x)?.Count ?? 0)
            .ThenBy(x => x, StringComparer.Ordinal).First();
    }
}

public sealed class EditorAssignmentHandler(
    BookFlowDbContext db, IEventTransaction transaction, IEditorSelectionPolicy selection) : IEventHandler
{
    public string Consumer => "editor-assignment";

    public Task HandleAsync(DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Uploaded);
        return transaction.ExecuteAsync(Consumer, message, async token =>
        {
            var manuscript = await EventLookup.ManuscriptAsync(db, message, token);
            if (manuscript.EditorId is not null)
                return;
            if (manuscript.Status != ManuscriptStatus.Submitted)
                throw new PermanentFailureException("Cannot assign an editor in this state.");
            manuscript.EditorId = await selection.SelectAsync(manuscript.TenantId, token);
            manuscript.Status = ManuscriptStatus.AssignedForEditing;
            manuscript.Sequence++;
            db.Enqueue(DomainEvent.Create(EventTypes.Assigned, new EventData(
                manuscript.TenantId, manuscript.Sequence, manuscript.Id, message.Data.VersionId)));
        }, ct);
    }
}
