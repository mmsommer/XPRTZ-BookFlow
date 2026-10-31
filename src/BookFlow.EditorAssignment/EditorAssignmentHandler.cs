using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.EditorAssignment;

/// <summary>Selects an available editor within a publisher tenant.</summary>
public interface IEditorSelectionPolicy
{
    /// <summary>Selects an available editor in the specified tenant.</summary>
    /// <param name="tenantId">The publisher tenant identifier.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<string> SelectAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Selects the least-loaded active editor with deterministic identifier tie-breaking.</summary>
/// <param name="db">The publishing workflow database context.</param>
public sealed class LeastLoadedEditorPolicy(BookFlowDbContext db) : IEditorSelectionPolicy
{
    /// <summary>Selects an available editor in the specified tenant.</summary>
    /// <param name="tenantId">The publisher tenant identifier.</param>
    /// <param name="ct">Cancels the operation.</param>
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

/// <summary>Assigns an editor to a newly uploaded manuscript and publishes the assignment.</summary>
/// <param name="db">The publishing workflow database context.</param>
/// <param name="transaction">The deduplicating consumer transaction service.</param>
/// <param name="selection">The tenant-scoped editor selection policy.</param>
public sealed class EditorAssignmentHandler(
    BookFlowDbContext db, IEventTransaction transaction, IEditorSelectionPolicy selection) : IEventHandler
{
    /// <summary>The event consumer name.</summary>
    public string Consumer => "editor-assignment";

    /// <summary>Assigns an editor once and queues the assignment event.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
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
