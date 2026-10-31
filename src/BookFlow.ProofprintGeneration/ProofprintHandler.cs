using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.ProofprintGeneration;

public sealed class ProofprintHandler(BookFlowDbContext db, IEventTransaction transaction,
    IBlobStore blobs, IPrintProvider provider) : IEventHandler
{
    public string Consumer => "proofprint-generation";

    public Task HandleAsync(DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Uploaded, EventTypes.Edited, EventTypes.Revised);
        return transaction.ExecuteAsync(Consumer, message, async token =>
        {
            var manuscript = await EventLookup.ManuscriptAsync(db, message, token);
            var version = await db.Versions.SingleOrDefaultAsync(
                x => x.Id == message.Data.VersionId && x.ManuscriptId == manuscript.Id, token)
                ?? throw new PermanentFailureException("Source version does not exist.");
            if (await db.Proofprints.AnyAsync(x => x.VersionId == version.Id, token))
                return;
            var source = await blobs.GetAsync("manuscripts", version.BlobName, token);
            var pdf = await provider.GenerateProofprintAsync(source, $"proofprint:{version.Id}", token);
            if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                throw new PermanentFailureException("The proofprint is not a PDF.");
            var name = $"{manuscript.TenantId}/{manuscript.Id}/{version.Id}.pdf";
            await blobs.PutAsync("proofprints", name, pdf, token);
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
