using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.WebApp;

public sealed class ManuscriptService(BookFlowDbContext db, IBlobStore blobs) : IManuscriptService
{
    public async Task<ParticipantResponse> ParticipantAsync(Actor actor, string id, CancellationToken ct)
    {
        actor.Require("Admin");
        var participant = await db.Participants.SingleOrDefaultAsync(x => x.TenantId == actor.TenantId && x.Id == id, ct)
            ?? throw new NotFoundException();
        return new ParticipantResponse(participant.Id, participant.Name, participant.Email, participant.Role, participant.Active);
    }

    public async Task RegisterAsync(Actor actor, CreateParticipantRequest request, CancellationToken ct)
    {
        actor.Require("Admin");
        if (await db.Participants.AnyAsync(x => x.TenantId == actor.TenantId && x.Id == request.Id, ct))
            throw new ConflictException("Participant already exists.");
        if (request.Role == "Publisher" &&
            await db.Participants.AnyAsync(x => x.TenantId == actor.TenantId && x.Role == "Publisher", ct))
            throw new ConflictException("Each tenant represents one publisher organization.");
        db.Participants.Add(new Participant
        {
            TenantId = actor.TenantId, Id = request.Id, Name = request.Name, Email = request.Email, Role = request.Role
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<ManuscriptResponse> CreateAsync(Actor actor, CreateManuscriptRequest request, CancellationToken ct)
    {
        actor.Require("Author");
        await RequireParticipantAsync(actor.TenantId, actor.Id, "Author", ct);
        await RequireParticipantAsync(actor.TenantId, request.PublisherId, "Publisher", ct);
        var key = Key(actor, "upload", request.IdempotencyKey);
        var hash = Hash(request);
        if (await ExistingAsync(key, hash, ct) is { } replay)
            return Map(await db.Manuscripts.SingleAsync(x => x.Id == replay, ct));
        var bytes = Decode(request.FileBase64);
        var reservation = await ReserveAsync(key, bytes, ct);
        await blobs.PutAsync("manuscripts", reservation.BlobName, bytes, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await ExistingAsync(key, hash, ct) is { } existing)
            return Map(await db.Manuscripts.SingleAsync(x => x.Id == existing, ct));
        var manuscript = new Manuscript
        {
            Id = Guid.NewGuid(), TenantId = actor.TenantId, AuthorId = actor.Id,
            PublisherId = request.PublisherId, Title = request.Title, Sequence = 1,
            SubmittedAt = DateTimeOffset.UtcNow, Status = ManuscriptStatus.Submitted
        };
        var version = new ManuscriptVersion
        {
            Id = Guid.NewGuid(), ManuscriptId = manuscript.Id, Number = 1,
            BlobName = reservation.BlobName, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Manuscripts.Add(manuscript);
        db.Versions.Add(version);
        db.Metadata.Add(new ManuscriptMetadata { ManuscriptId = manuscript.Id });
        reservation.State = "Committed";
        Remember(key, hash, manuscript.Id);
        db.Enqueue(DomainEvent.Create(EventTypes.Uploaded, new EventData(actor.TenantId, 1, manuscript.Id, version.Id)));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Map(manuscript);
    }

    public async Task<VersionResponse> VersionAsync(Actor actor, Guid id, CreateVersionRequest request, bool editor, CancellationToken ct)
    {
        actor.Require(editor ? "Editor" : "Author");
        var manuscript = await AccessibleAsync(actor, id, ct);
        if (editor ? manuscript.EditorId != actor.Id : manuscript.AuthorId != actor.Id)
            throw new AccessDeniedException();
        var key = Key(actor, $"version:{id}:{editor}", request.IdempotencyKey);
        var hash = Hash(request);
        if (await ExistingAsync(key, hash, ct) is { } existing)
            return await MapVersionAsync(await db.Versions.SingleAsync(x => x.Id == existing, ct), ct);
        if (editor ? manuscript.Status is not (ManuscriptStatus.AssignedForEditing or ManuscriptStatus.AwaitingEditorReview) :
            manuscript.Status != ManuscriptStatus.AwaitingAuthorResponse)
            throw new ConflictException("Manuscript is not awaiting this action.");
        if (!editor && string.IsNullOrWhiteSpace(request.Feedback))
            throw new ArgumentException("Author feedback is required.");
        if (!editor && request.Highlights is { Length: > 0 })
            throw new ArgumentException("Authors cannot supply editor highlights.");
        var parent = await db.Versions.SingleOrDefaultAsync(x => x.Id == request.ParentVersionId && x.ManuscriptId == id, ct)
            ?? throw new NotFoundException();
        if (await db.Versions.AnyAsync(x => x.ManuscriptId == id && x.Number > parent.Number, ct))
            throw new ConflictException("The parent is not the current version.");
        foreach (var highlight in request.Highlights ?? [])
        {
            if (highlight.StartOffset < 0 || highlight.EndOffset <= highlight.StartOffset ||
                string.IsNullOrWhiteSpace(highlight.OriginalText) || string.IsNullOrWhiteSpace(highlight.EditedText))
                throw new ArgumentException("Invalid highlight.");
        }
        var bytes = Decode(request.FileBase64);
        var reservation = await ReserveAsync(key, bytes, ct);
        await blobs.PutAsync("manuscripts", reservation.BlobName, bytes, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.Entry(manuscript).ReloadAsync(ct);
        if (await ExistingAsync(key, hash, ct) is { } replay)
            return await MapVersionAsync(await db.Versions.SingleAsync(x => x.Id == replay, ct), ct);
        if (await db.Versions.AnyAsync(x => x.ManuscriptId == id && x.Number > parent.Number, ct) ||
            (editor ? manuscript.Status is not (ManuscriptStatus.AssignedForEditing or ManuscriptStatus.AwaitingEditorReview) :
                manuscript.Status != ManuscriptStatus.AwaitingAuthorResponse))
            throw new ConflictException("The manuscript changed while uploading.");
        var version = new ManuscriptVersion
        {
            Id = Guid.NewGuid(), ManuscriptId = id, Number = parent.Number + 1,
            ParentId = parent.Id, BlobName = reservation.BlobName, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Versions.Add(version);
        foreach (var highlight in request.Highlights ?? [])
            db.Highlights.Add(new ManuscriptHighlight
            {
                Id = Guid.NewGuid(), VersionId = version.Id, StartOffset = highlight.StartOffset, EndOffset = highlight.EndOffset,
                OriginalText = highlight.OriginalText, EditedText = highlight.EditedText
            });
        if (!editor)
            db.Feedback.Add(new AuthorFeedback
            {
                Id = Guid.NewGuid(), VersionId = version.Id, AuthorId = actor.Id,
                Comment = request.Feedback!, SubmittedAt = DateTimeOffset.UtcNow
            });
        manuscript.Status = editor ? ManuscriptStatus.AwaitingAuthorResponse : ManuscriptStatus.AwaitingEditorReview;
        manuscript.Sequence++;
        reservation.State = "Committed";
        Remember(key, hash, version.Id);
        db.Enqueue(DomainEvent.Create(editor ? EventTypes.Edited : EventTypes.Revised,
            new EventData(actor.TenantId, manuscript.Sequence, id, version.Id)));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await MapVersionAsync(version, ct);
    }

    public async Task<BookResponse> ApproveAsync(Actor actor, Guid id, ApproveManuscriptRequest request, CancellationToken ct)
    {
        actor.Require("Editor");
        ValidateQuantity(request.Quantity);
        ValidateIsbn(request.Isbn);
        var manuscript = await AccessibleAsync(actor, id, ct);
        if (manuscript.EditorId != actor.Id)
            throw new AccessDeniedException();
        var key = Key(actor, $"approve:{id}", request.IdempotencyKey);
        var hash = Hash(request);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.Entry(manuscript).ReloadAsync(ct);
        if (await ExistingAsync(key, hash, ct) is { } existing)
            return Map(await db.Books.SingleAsync(x => x.Id == existing, ct));
        if (manuscript.Status is not (ManuscriptStatus.AssignedForEditing or ManuscriptStatus.AwaitingEditorReview))
            throw new ConflictException("Manuscript cannot be approved in this state.");
        await RequireParticipantAsync(actor.TenantId, request.DistributorId, "Distributor", ct);
        if (await db.Books.AnyAsync(x => x.Isbn == request.Isbn, ct))
            throw new ConflictException("ISBN is already registered.");
        var book = new Book
        {
            Id = Guid.NewGuid(), ManuscriptId = id, TenantId = actor.TenantId, PublisherId = manuscript.PublisherId,
            Title = manuscript.Title, Isbn = request.Isbn, Orderable = true
        };
        db.Books.Add(book);
        var order = NewOrder(book.Id, request.DistributorId, request.Quantity);
        manuscript.Status = ManuscriptStatus.Approved;
        manuscript.Sequence++;
        Remember(key, hash, book.Id);
        var version = await db.Versions.Where(x => x.ManuscriptId == id).OrderByDescending(x => x.Number).FirstAsync(ct);
        db.Enqueue(DomainEvent.Create(EventTypes.Approved,
            new EventData(actor.TenantId, manuscript.Sequence, id, version.Id, book.Id, order.DistributorId, order.Id, order.Quantity)));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Map(book);
    }

    public async Task<PrintOrderResponse> OrderAsync(Actor actor, CreatePrintOrderRequest request, CancellationToken ct)
    {
        actor.Require("Publisher");
        ValidateQuantity(request.Quantity);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var book = await AccessibleBookAsync(actor, request.BookId, ct);
        if (book.PublisherId != actor.Id)
            throw new AccessDeniedException();
        var key = Key(actor, "order", request.IdempotencyKey);
        var hash = Hash(request);
        if (await ExistingAsync(key, hash, ct) is { } existing)
            return Map(await db.PrintOrders.SingleAsync(x => x.Id == existing, ct));
        if (!book.Orderable)
            throw new ConflictException("Book is not orderable.");
        await RequireParticipantAsync(actor.TenantId, request.DistributorId, "Distributor", ct);
        var order = NewOrder(book.Id, request.DistributorId, request.Quantity);
        Remember(key, hash, order.Id);
        db.Enqueue(DomainEvent.Create(EventTypes.Order,
            new EventData(actor.TenantId, 1, BookId: book.Id, DistributorId: order.DistributorId,
                PrintOrderId: order.Id, Quantity: order.Quantity)));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Map(order);
    }

    public async Task<ManuscriptResponse> GetAsync(Actor actor, Guid id, CancellationToken ct) =>
        Map(await AccessibleAsync(actor, id, ct));

    public async Task<IReadOnlyList<ManuscriptResponse>> ListAsync(Actor actor, CancellationToken ct)
    {
        var manuscripts = await db.Manuscripts.AsNoTracking().Where(x => x.TenantId == actor.TenantId &&
            (actor.Role == "Admin" || actor.Role == "Author" && x.AuthorId == actor.Id ||
             actor.Role == "Editor" && x.EditorId == actor.Id || actor.Role == "Publisher" && x.PublisherId == actor.Id))
            .OrderBy(x => x.Id).Take(100).ToListAsync(ct);
        return manuscripts.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<VersionResponse>> VersionsAsync(Actor actor, Guid id, CancellationToken ct)
    {
        await AccessibleAsync(actor, id, ct);
        var versions = await db.Versions.AsNoTracking().Where(x => x.ManuscriptId == id)
            .OrderBy(x => x.Number).ToListAsync(ct);
        var versionIds = versions.Select(x => x.Id).ToArray();
        var highlights = await db.Highlights.AsNoTracking().Where(x => versionIds.Contains(x.VersionId))
            .Select(x => new { x.VersionId, Highlight = new HighlightRequest(x.StartOffset, x.EndOffset, x.OriginalText, x.EditedText) })
            .ToListAsync(ct);
        var feedback = await db.Feedback.AsNoTracking().Where(x => versionIds.Contains(x.VersionId))
            .Select(x => new { x.VersionId, x.Comment }).ToListAsync(ct);
        var proofprints = await db.Proofprints.AsNoTracking().Where(x => versionIds.Contains(x.VersionId))
            .Select(x => new { x.VersionId, x.Id }).ToListAsync(ct);
        var highlightsByVersion = highlights.GroupBy(x => x.VersionId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<HighlightRequest>)x.Select(y => y.Highlight).ToArray());
        var feedbackByVersion = feedback.GroupBy(x => x.VersionId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(y => y.Comment).ToArray());
        var proofprintByVersion = proofprints.ToDictionary(x => x.VersionId, x => (Guid?)x.Id);

        return versions.Select(version => new VersionResponse(
            version.Id, version.Number, version.ParentId, version.CreatedAt,
            highlightsByVersion.GetValueOrDefault(version.Id) ?? Array.Empty<HighlightRequest>(),
            feedbackByVersion.GetValueOrDefault(version.Id) ?? Array.Empty<string>(),
            proofprintByVersion.GetValueOrDefault(version.Id))).ToArray();
    }

    public async Task<IReadOnlyList<HistoryResponse>> HistoryAsync(Actor actor, Guid id, CancellationToken ct)
    {
        await AccessibleAsync(actor, id, ct);
        return await db.History.Where(x => x.ManuscriptId == id).OrderBy(x => x.Sequence)
            .Select(x => new HistoryResponse(x.EventId, x.Sequence, x.Status, x.ChangedAt)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BookResponse>> BooksAsync(Actor actor, CancellationToken ct)
    {
        return await AuthorizedBooks(actor).OrderBy(x => x.Id)
            .Select(x => new BookResponse(x.Id, x.ManuscriptId, x.Title, x.Isbn, x.Orderable))
            .ToListAsync(ct);
    }

    public async Task<BookResponse> BookAsync(Actor actor, Guid id, CancellationToken ct) =>
        await AuthorizedBooks(actor).Where(x => x.Id == id)
            .Select(x => new BookResponse(x.Id, x.ManuscriptId, x.Title, x.Isbn, x.Orderable))
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException();

    public async Task<PrintOrderResponse> GetOrderAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var order = await db.PrintOrders.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        await AccessibleBookAsync(actor, order.BookId, ct);
        if (actor.Role == "Distributor" && actor.Id != order.DistributorId)
            throw new AccessDeniedException();
        return Map(order);
    }

    public async Task<IReadOnlyList<StockResponse>> StockAsync(Actor actor, Guid bookId, CancellationToken ct)
    {
        await AccessibleBookAsync(actor, bookId, ct);
        return await db.Stock.Where(x => x.BookId == bookId && (actor.Role != "Distributor" || x.DistributorId == actor.Id))
            .Select(x => new StockResponse(x.BookId, x.DistributorId, x.AvailableQuantity,
                x.PrintedQuantity, x.DeliveredQuantity, x.DistributedQuantity, x.SoldQuantity, x.Sequence, x.Alert, x.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<byte[]> FileAsync(Actor actor, Guid manuscriptId, Guid fileId, bool proofprint, CancellationToken ct)
    {
        await AccessibleAsync(actor, manuscriptId, ct);
        if (!proofprint)
        {
            var version = await db.Versions.SingleOrDefaultAsync(x => x.Id == fileId && x.ManuscriptId == manuscriptId, ct)
                ?? throw new NotFoundException();
            return await blobs.GetAsync("manuscripts", version.BlobName, ct);
        }
        var pdf = await db.Proofprints.SingleOrDefaultAsync(x => x.Id == fileId, ct) ?? throw new NotFoundException();
        if (!await db.Versions.AnyAsync(x => x.Id == pdf.VersionId && x.ManuscriptId == manuscriptId, ct))
            throw new NotFoundException();
        return await blobs.GetAsync("proofprints", pdf.BlobName, ct);
    }

    private async Task<Manuscript> AccessibleAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var manuscript = await db.Manuscripts.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == actor.TenantId, ct)
            ?? throw new NotFoundException();
        if (actor.Role != "Admin" && !(actor.Role == "Author" && manuscript.AuthorId == actor.Id) &&
            !(actor.Role == "Editor" && manuscript.EditorId == actor.Id) &&
            !(actor.Role == "Publisher" && manuscript.PublisherId == actor.Id))
            throw new AccessDeniedException();
        return manuscript;
    }

    private async Task<Book> AccessibleBookAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var book = await db.Books.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == actor.TenantId, ct)
            ?? throw new NotFoundException();
        if (actor.Role != "Distributor")
            await AccessibleAsync(actor, book.ManuscriptId, ct);
        else
            await RequireParticipantAsync(actor.TenantId, actor.Id, "Distributor", ct);
        return book;
    }

    private IQueryable<Book> AuthorizedBooks(Actor actor)
    {
        var books = db.Books.AsNoTracking().Where(x => x.TenantId == actor.TenantId);
        if (actor.Role == "Distributor")
            return books;

        return books.Where(book => db.Manuscripts.Any(manuscript =>
            manuscript.Id == book.ManuscriptId && manuscript.TenantId == actor.TenantId &&
            (actor.Role == "Admin" ||
             actor.Role == "Author" && manuscript.AuthorId == actor.Id ||
             actor.Role == "Editor" && manuscript.EditorId == actor.Id ||
             actor.Role == "Publisher" && manuscript.PublisherId == actor.Id)));
    }

    private async Task RequireParticipantAsync(Guid tenant, string id, string role, CancellationToken ct)
    {
        if (!await db.Participants.AnyAsync(x => x.TenantId == tenant && x.Id == id && x.Role == role && x.Active, ct))
            throw new AccessDeniedException();
    }

    private async Task<UploadReservation> ReserveAsync(string key, byte[] bytes, CancellationToken ct)
    {
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
        var existing = await db.Uploads.FindAsync([id], ct);
        if (existing is not null)
            return existing;
        var reservation = new UploadReservation
        {
            Id = id, BlobName = $"{id}/{Convert.ToHexString(SHA256.HashData(bytes))}.bin",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Uploads.Add(reservation);
        await db.SaveChangesAsync(ct);
        return reservation;
    }

    private static string Key(Actor actor, string operation, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            throw new ArgumentException("An idempotency key of at most 100 characters is required.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{actor.TenantId}:{actor.Id}:{operation}:{key}")));
    }

    private static string Hash<T>(T request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, EventJson.Options)));

    private async Task<Guid?> ExistingAsync(string key, string hash, CancellationToken ct)
    {
        var record = await db.Idempotency.FindAsync([key], ct);
        if (record is null)
            return null;
        if (record.RequestHash != hash)
            throw new ConflictException("The idempotency key was used for a different request.");
        return record.ResourceId;
    }

    private void Remember(string key, string hash, Guid id) =>
        db.Idempotency.Add(new IdempotencyRecord { Key = key, RequestHash = hash, ResourceId = id });

    private PrintOrder NewOrder(Guid book, string distributor, int quantity)
    {
        var order = new PrintOrder
        {
            Id = Guid.NewGuid(), BookId = book, DistributorId = distributor, Quantity = quantity,
            Status = PrintOrderStatus.Requested, CreatedAt = DateTimeOffset.UtcNow
        };
        db.PrintOrders.Add(order);
        return order;
    }

    private static byte[] Decode(string base64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new ArgumentException("Invalid base64 manuscript."); }
        if (bytes.Length is < 1 or > 10000000)
            throw new ArgumentException("The manuscript must be between 1 byte and 10 MB.");
        return bytes;
    }

    private static void ValidateQuantity(int quantity)
    {
        if (quantity is < 1 or > 1000000)
            throw new ArgumentException("Invalid print quantity.");
    }

    public static void ValidateIsbn(string isbn)
    {
        if (isbn.Length != 13 || isbn.Any(x => !char.IsAsciiDigit(x)) ||
            isbn.Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum() % 10 != 0)
            throw new ArgumentException("Invalid ISBN-13 checksum.");
    }

    private static ManuscriptResponse Map(Manuscript x) =>
        new(x.Id, x.Title, x.AuthorId, x.PublisherId, x.EditorId, x.Status, x.ProgressStatus, x.ProgressSequence, x.SubmittedAt);
    private static BookResponse Map(Book x) => new(x.Id, x.ManuscriptId, x.Title, x.Isbn, x.Orderable);
    private static PrintOrderResponse Map(PrintOrder x) => new(x.Id, x.BookId, x.DistributorId, x.Quantity, x.Status, x.ExternalReference);
    private async Task<VersionResponse> MapVersionAsync(ManuscriptVersion x, CancellationToken ct) =>
        new(x.Id, x.Number, x.ParentId, x.CreatedAt,
            await db.Highlights.Where(h => h.VersionId == x.Id)
                .Select(h => new HighlightRequest(h.StartOffset, h.EndOffset, h.OriginalText, h.EditedText)).ToListAsync(ct),
            await db.Feedback.Where(f => f.VersionId == x.Id).Select(f => f.Comment).ToListAsync(ct),
            await db.Proofprints.Where(p => p.VersionId == x.Id).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct));
}
