using System.ComponentModel.DataAnnotations;
using BookFlow.Core;

namespace BookFlow.WebApp;

/// <summary>Registers a participant within the caller's publisher tenant.</summary>
public sealed record CreateParticipantRequest(
    [property: Required, MaxLength(100)] string Id,
    [property: Required, MaxLength(200)] string Name,
    [property: Required, EmailAddress] string Email,
    [property: Required, RegularExpression("^(Author|Editor|Publisher|Distributor)$")] string Role);

/// <summary>Creates a manuscript with its immutable initial file version.</summary>
public sealed record CreateManuscriptRequest(
    [property: Required, MaxLength(200)] string Title,
    [property: Required, MaxLength(100)] string PublisherId,
    [property: Required, MaxLength(100)] string IdempotencyKey,
    [property: Required, MaxLength(15000000)] string FileBase64);

/// <summary>Represents an editor highlight in the parent version's text.</summary>
public sealed record HighlightRequest(
    [property: Range(0, int.MaxValue)] int StartOffset,
    [property: Range(0, int.MaxValue)] int EndOffset,
    [property: Required, MaxLength(10000)] string OriginalText,
    [property: Required, MaxLength(10000)] string EditedText);

/// <summary>Creates an edited manuscript version or an author revision.</summary>
public sealed record CreateVersionRequest(
    Guid ParentVersionId,
    [property: Required, MaxLength(100)] string IdempotencyKey,
    [property: Required, MaxLength(15000000)] string FileBase64,
    [property: MaxLength(10000)] string? Feedback,
    HighlightRequest[]? Highlights);

/// <summary>Approves a manuscript and requests its initial print run.</summary>
public sealed record ApproveManuscriptRequest(
    [property: Required, RegularExpression("^[0-9]{13}$")] string Isbn,
    [property: Required, MaxLength(100)] string DistributorId,
    [property: Range(1, 1000000)] int Quantity,
    [property: Required, MaxLength(100)] string IdempotencyKey);

/// <summary>Requests additional copies of an approved book.</summary>
public sealed record CreatePrintOrderRequest(
    Guid BookId,
    [property: Required, MaxLength(100)] string DistributorId,
    [property: Range(1, 1000000)] int Quantity,
    [property: Required, MaxLength(100)] string IdempotencyKey);

/// <summary>Returns a manuscript's authoritative and projected workflow state.</summary>
public sealed record ManuscriptResponse(Guid Id, string Title, string AuthorId, string PublisherId, string? EditorId,
    ManuscriptStatus Status, ManuscriptStatus ProgressStatus, long ProgressSequence, DateTimeOffset SubmittedAt);

/// <summary>Returns a version and the edits/feedback attached to it.</summary>
public sealed record VersionResponse(Guid Id, int Number, Guid? ParentId, DateTimeOffset CreatedAt,
    IReadOnlyList<HighlightRequest> Highlights, IReadOnlyList<string> Feedback, Guid? ProofprintId);

/// <summary>Returns a persisted workflow-history entry.</summary>
public sealed record HistoryResponse(string EventId, long Sequence, ManuscriptStatus Status, DateTimeOffset ChangedAt);

/// <summary>Returns a published book and its orderability.</summary>
public sealed record BookResponse(Guid Id, Guid ManuscriptId, string Title, string Isbn, bool Orderable);

/// <summary>Returns a print order and its external tracking state.</summary>
public sealed record PrintOrderResponse(Guid Id, Guid BookId, string DistributorId, int Quantity, PrintOrderStatus Status,
    string? ExternalReference);

/// <summary>Returns stock totals for a distributor and title.</summary>
public sealed record StockResponse(Guid BookId, string DistributorId, int AvailableQuantity, int PrintedQuantity,
    int DeliveredQuantity, int DistributedQuantity, int SoldQuantity, long Sequence, string? Alert, DateTimeOffset UpdatedAt);

/// <summary>Returns participant registration details for tenant administrators.</summary>
public sealed record ParticipantResponse(string Id, string Name, string Email, string Role, bool Active);

/// <summary>Identifies a participant registration created by an administrator.</summary>
public sealed record CreateParticipantResponse(string Id);

public interface IManuscriptService
{
    Task RegisterAsync(Actor actor, CreateParticipantRequest request, CancellationToken ct);
    Task<ParticipantResponse> ParticipantAsync(Actor actor, string id, CancellationToken ct);
    Task<ManuscriptResponse> CreateAsync(Actor actor, CreateManuscriptRequest request, CancellationToken ct);
    Task<VersionResponse> VersionAsync(Actor actor, Guid id, CreateVersionRequest request, bool editor, CancellationToken ct);
    Task<BookResponse> ApproveAsync(Actor actor, Guid id, ApproveManuscriptRequest request, CancellationToken ct);
    Task<PrintOrderResponse> OrderAsync(Actor actor, CreatePrintOrderRequest request, CancellationToken ct);
    Task<ManuscriptResponse> GetAsync(Actor actor, Guid id, CancellationToken ct);
    Task<IReadOnlyList<ManuscriptResponse>> ListAsync(Actor actor, CancellationToken ct);
    Task<IReadOnlyList<VersionResponse>> VersionsAsync(Actor actor, Guid id, CancellationToken ct);
    Task<IReadOnlyList<HistoryResponse>> HistoryAsync(Actor actor, Guid id, CancellationToken ct);
    Task<IReadOnlyList<BookResponse>> BooksAsync(Actor actor, CancellationToken ct);
    Task<PrintOrderResponse> GetOrderAsync(Actor actor, Guid id, CancellationToken ct);
    Task<IReadOnlyList<StockResponse>> StockAsync(Actor actor, Guid bookId, CancellationToken ct);
    Task<byte[]> FileAsync(Actor actor, Guid manuscriptId, Guid fileId, bool proofprint, CancellationToken ct);
}
