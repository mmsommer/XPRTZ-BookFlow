using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.ProofprintGeneration;

/// <summary>Generates and stores a version's proofprint before publishing its availability.</summary>
/// <param name="db">The publishing workflow database context.</param>
/// <param name="transaction">The deduplicating consumer transaction service.</param>
/// <param name="blobs">The manuscript and proofprint blob store.</param>
/// <param name="provider">The print provider used for proofprints or print orders.</param>
public sealed class ProofprintHandler(BookFlowDbContext db, IEventTransaction transaction,
    IBlobStore blobs, IPrintProvider provider) : IEventHandler
{
    /// <summary>The event consumer name.</summary>
    public string Consumer => "proofprint-generation";

    /// <summary>Stores the event version's PDF proofprint and queues its creation event.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    public async Task HandleAsync(DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Uploaded, EventTypes.Edited, EventTypes.Revised);
        if (await db.Inbox.AnyAsync(x => x.Consumer == Consumer && x.EventId == message.Id, ct))
            return;

        var manuscript = await EventLookup.ManuscriptAsync(db, message, ct);
        var version = await db.Versions.SingleOrDefaultAsync(
            x => x.Id == message.Data.VersionId && x.ManuscriptId == manuscript.Id, ct)
            ?? throw new PermanentFailureException("Source version does not exist.");
        if (await db.Proofprints.AnyAsync(x => x.VersionId == version.Id, ct))
        {
            await transaction.ExecuteAsync(Consumer, message, _ => Task.CompletedTask, ct);
            return;
        }

        var source = await blobs.GetAsync("manuscripts", version.BlobName, ct);
        var pdf = await provider.GenerateProofprintAsync(source, $"proofprint:{version.Id}", ct);
        if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new PermanentFailureException("The proofprint is not a PDF.");
        var name = $"{manuscript.TenantId}/{manuscript.Id}/{version.Id}.pdf";
        await blobs.PutAsync("proofprints", name, pdf, ct);

        await transaction.ExecuteAsync(Consumer, message, async token =>
        {
            if (await db.Proofprints.AnyAsync(x => x.VersionId == version.Id, token))
                return;
            var proofprint = new Proofprint
            {
                Id = Guid.NewGuid(), VersionId = version.Id, BlobName = name, CreatedAt = DateTimeOffset.UtcNow
            };
            db.Proofprints.Add(proofprint);
            db.Enqueue(DomainEvent.Create(EventTypes.Proofprint,
                new EventData(manuscript.TenantId, message.Data.Sequence, manuscript.Id,
                    version.Id, ProofprintId: proofprint.Id)));
        }, ct);
    }
}
