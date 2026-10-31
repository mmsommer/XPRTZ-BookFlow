using System.ComponentModel.DataAnnotations;
using BookFlow.Core;

namespace BookFlow.WebApp;

/// <summary>Registers a participant within the caller's publisher tenant.</summary>
/// <param name="Id">The participant identifier within the caller's tenant.</param>
/// <param name="Name">The participant's display name.</param>
/// <param name="Email">The participant's email address.</param>
/// <param name="Role">The participant's authorization role.</param>
public sealed record CreateParticipantRequest(
    [property: Required, MaxLength(100)] string Id,
    [property: Required, MaxLength(200)] string Name,
    [property: Required, EmailAddress] string Email,
    [property: Required, RegularExpression("^(Author|Editor|Publisher|Distributor)$")] string Role);

/// <summary>Creates a manuscript with its immutable initial file version.</summary>
/// <param name="Title">The manuscript title.</param>
/// <param name="PublisherId">The publisher's participant identifier.</param>
/// <param name="IdempotencyKey">The stable request key used to deduplicate matching retries.</param>
/// <param name="FileBase64">The manuscript file content encoded as Base64.</param>
public sealed record CreateManuscriptRequest(
    [property: Required, MaxLength(200)] string Title,
    [property: Required, MaxLength(100)] string PublisherId,
    [property: Required, MaxLength(100)] string IdempotencyKey,
    [property: Required, MaxLength(15000000)] string FileBase64);

/// <summary>Represents an editor highlight in the parent version's text.</summary>
/// <param name="StartOffset">The inclusive start offset of the highlighted text.</param>
/// <param name="EndOffset">The end offset of the highlighted text.</param>
/// <param name="OriginalText">The text being replaced.</param>
/// <param name="EditedText">The editor's replacement text.</param>
public sealed record HighlightRequest(
    [property: Range(0, int.MaxValue)] int StartOffset,
    [property: Range(0, int.MaxValue)] int EndOffset,
    [property: Required, MaxLength(10000)] string OriginalText,
    [property: Required, MaxLength(10000)] string EditedText);

/// <summary>Creates an edited manuscript version or an author revision.</summary>
/// <param name="ParentVersionId">The manuscript version on which the new version is based.</param>
/// <param name="IdempotencyKey">The stable request key used to deduplicate matching retries.</param>
/// <param name="FileBase64">The manuscript file content encoded as Base64.</param>
/// <param name="Feedback">The author's revision feedback, if supplied.</param>
/// <param name="Highlights">The editor's text highlights for this version.</param>
public sealed record CreateVersionRequest(
    Guid ParentVersionId,
    [property: Required, MaxLength(100)] string IdempotencyKey,
    [property: Required, MaxLength(15000000)] string FileBase64,
    [property: MaxLength(10000)] string? Feedback,
    HighlightRequest[]? Highlights);

/// <summary>Approves a manuscript and requests its initial print run.</summary>
/// <param name="Isbn">The book's thirteen-digit ISBN.</param>
/// <param name="DistributorId">The distributor's participant identifier.</param>
/// <param name="Quantity">The number of copies in the initial print run.</param>
/// <param name="IdempotencyKey">The stable request key used to deduplicate matching retries.</param>
public sealed record ApproveManuscriptRequest(
    [property: Required, RegularExpression("^[0-9]{13}$")] string Isbn,
    [property: Required, MaxLength(100)] string DistributorId,
    [property: Range(1, 1000000)] int Quantity,
    [property: Required, MaxLength(100)] string IdempotencyKey);

/// <summary>Requests additional copies of an approved book.</summary>
/// <param name="BookId">The associated book identifier.</param>
/// <param name="DistributorId">The distributor's participant identifier.</param>
/// <param name="Quantity">The number of additional copies to print.</param>
/// <param name="IdempotencyKey">The stable request key used to deduplicate matching retries.</param>
public sealed record CreatePrintOrderRequest(
    Guid BookId,
    [property: Required, MaxLength(100)] string DistributorId,
    [property: Range(1, 1000000)] int Quantity,
    [property: Required, MaxLength(100)] string IdempotencyKey);

/// <summary>Returns a manuscript's authoritative and projected workflow state.</summary>
/// <param name="Id">The manuscript identifier.</param>
/// <param name="Title">The manuscript title.</param>
/// <param name="AuthorId">The manuscript author's participant identifier.</param>
/// <param name="PublisherId">The publisher's participant identifier.</param>
/// <param name="EditorId">The assigned editor's participant identifier, if any.</param>
/// <param name="Status">The authoritative workflow status.</param>
/// <param name="ProgressStatus">The status projected from contiguous workflow history.</param>
/// <param name="ProgressSequence">The last contiguous sequence applied to the progress projection.</param>
/// <param name="SubmittedAt">The time the manuscript was submitted.</param>
public sealed record ManuscriptResponse(Guid Id, string Title, string AuthorId, string PublisherId, string? EditorId,
    ManuscriptStatus Status, ManuscriptStatus ProgressStatus, long ProgressSequence, DateTimeOffset SubmittedAt);

/// <summary>Returns a version and the edits/feedback attached to it.</summary>
/// <param name="Id">The manuscript version identifier.</param>
/// <param name="Number">The manuscript version number.</param>
/// <param name="ParentId">The parent manuscript version identifier, if any.</param>
/// <param name="CreatedAt">The time this record was created.</param>
/// <param name="Highlights">The editor's text highlights for this version.</param>
/// <param name="Feedback">The author feedback attached to this version.</param>
/// <param name="ProofprintId">The associated proofprint identifier, if any.</param>
public sealed record VersionResponse(Guid Id, int Number, Guid? ParentId, DateTimeOffset CreatedAt,
    IReadOnlyList<HighlightRequest> Highlights, IReadOnlyList<string> Feedback, Guid? ProofprintId);

/// <summary>Returns a persisted workflow-history entry.</summary>
/// <param name="EventId">The domain event identifier.</param>
/// <param name="Sequence">The aggregate event sequence number.</param>
/// <param name="Status">The manuscript status recorded by this event.</param>
/// <param name="ChangedAt">The time of the workflow status change.</param>
public sealed record HistoryResponse(string EventId, long Sequence, ManuscriptStatus Status, DateTimeOffset ChangedAt);

/// <summary>Returns a published book and its orderability.</summary>
/// <param name="Id">The published book identifier.</param>
/// <param name="ManuscriptId">The associated manuscript identifier.</param>
/// <param name="Title">The published book title.</param>
/// <param name="Isbn">The book's thirteen-digit ISBN.</param>
/// <param name="Orderable">Whether additional copies may be ordered.</param>
public sealed record BookResponse(Guid Id, Guid ManuscriptId, string Title, string Isbn, bool Orderable);

/// <summary>Returns a print order and its external tracking state.</summary>
/// <param name="Id">The print order identifier.</param>
/// <param name="BookId">The associated book identifier.</param>
/// <param name="DistributorId">The distributor's participant identifier.</param>
/// <param name="Quantity">The number of copies ordered.</param>
/// <param name="Status">The print order's fulfillment status.</param>
/// <param name="ExternalReference">The print provider's order reference, if assigned.</param>
public sealed record PrintOrderResponse(Guid Id, Guid BookId, string DistributorId, int Quantity, PrintOrderStatus Status,
    string? ExternalReference);

/// <summary>Returns stock totals for a distributor and title.</summary>
/// <param name="BookId">The associated book identifier.</param>
/// <param name="DistributorId">The distributor's participant identifier.</param>
/// <param name="AvailableQuantity">The number of copies currently available.</param>
/// <param name="PrintedQuantity">The number of copies recorded as printed.</param>
/// <param name="DeliveredQuantity">The number of copies recorded as delivered.</param>
/// <param name="DistributedQuantity">The number of copies recorded as distributed.</param>
/// <param name="SoldQuantity">The number of copies recorded as sold.</param>
/// <param name="Sequence">The aggregate event sequence number.</param>
/// <param name="Alert">The latest inventory alert type, if any.</param>
/// <param name="UpdatedAt">The time the stock projection was last updated.</param>
public sealed record StockResponse(Guid BookId, string DistributorId, int AvailableQuantity, int PrintedQuantity,
    int DeliveredQuantity, int DistributedQuantity, int SoldQuantity, long Sequence, string? Alert, DateTimeOffset UpdatedAt);

/// <summary>Returns participant registration details for tenant administrators.</summary>
/// <param name="Id">The participant identifier within the tenant.</param>
/// <param name="Name">The participant's display name.</param>
/// <param name="Email">The participant's email address.</param>
/// <param name="Role">The participant's authorization role.</param>
/// <param name="Active">Whether the participant is active.</param>
public sealed record ParticipantResponse(string Id, string Name, string Email, string Role, bool Active);

/// <summary>Identifies a participant registration created by an administrator.</summary>
/// <param name="Id">The newly registered participant identifier.</param>
public sealed record CreateParticipantResponse(string Id);

/// <summary>Provides tenant-scoped manuscript, participant, book, and print-order operations.</summary>
public interface IManuscriptService
{
    /// <summary>Registers a participant in the administrator's tenant.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="request">The participant registration details.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task RegisterAsync(Actor actor, CreateParticipantRequest request, CancellationToken ct);
    /// <summary>Returns participant registration details after administrator authorization.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="id">The participant identifier within the caller's tenant.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<ParticipantResponse> ParticipantAsync(Actor actor, string id, CancellationToken ct);
    /// <summary>Creates a manuscript and its immutable initial file, deduplicating matching retries.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="request">The manuscript title, publisher, initial file, and idempotency key.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<ManuscriptResponse> CreateAsync(Actor actor, CreateManuscriptRequest request, CancellationToken ct);
    /// <summary>Creates an editor version or author revision with an immutable file and attached annotations.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="id">The manuscript identifier.</param>
    /// <param name="request">The parent version, new file, feedback, highlights, and idempotency key.</param>
    /// <param name="editor">Whether this is an editor version rather than an author revision.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<VersionResponse> VersionAsync(Actor actor, Guid id, CreateVersionRequest request, bool editor, CancellationToken ct);
    /// <summary>Approves an editor-reviewed manuscript and creates its book and initial print order.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="id">The manuscript identifier.</param>
    /// <param name="request">The ISBN, distributor, initial copy count, and idempotency key.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<BookResponse> ApproveAsync(Actor actor, Guid id, ApproveManuscriptRequest request, CancellationToken ct);
    /// <summary>Creates an authorized additional print order and queues its request event.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="request">The book, distributor, additional copy count, and idempotency key.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<PrintOrderResponse> OrderAsync(Actor actor, CreatePrintOrderRequest request, CancellationToken ct);
    /// <summary>Returns a manuscript after enforcing caller and tenant authorization.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="id">The manuscript identifier.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<ManuscriptResponse> GetAsync(Actor actor, Guid id, CancellationToken ct);
    /// <summary>Lists manuscripts visible to the caller.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<IReadOnlyList<ManuscriptResponse>> ListAsync(Actor actor, CancellationToken ct);
    /// <summary>Returns a manuscript's versions with highlights, feedback, and proofprint identifiers.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="id">The manuscript identifier.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<IReadOnlyList<VersionResponse>> VersionsAsync(Actor actor, Guid id, CancellationToken ct);
    /// <summary>Returns the authorized manuscript's sequenced workflow history.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="id">The manuscript identifier.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<IReadOnlyList<HistoryResponse>> HistoryAsync(Actor actor, Guid id, CancellationToken ct);
    /// <summary>Returns a published book after enforcing caller and tenant authorization.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="id">The published book identifier.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<BookResponse> BookAsync(Actor actor, Guid id, CancellationToken ct);
    /// <summary>Lists published books visible to the caller.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<IReadOnlyList<BookResponse>> BooksAsync(Actor actor, CancellationToken ct);
    /// <summary>Returns an authorized print order and its provider tracking state.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="id">The print order identifier.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<PrintOrderResponse> GetOrderAsync(Actor actor, Guid id, CancellationToken ct);
    /// <summary>Returns authorized stock projections for a book.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="bookId">The book identifier.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<IReadOnlyList<StockResponse>> StockAsync(Actor actor, Guid bookId, CancellationToken ct);
    /// <summary>Downloads an authorized manuscript version or proofprint file.</summary>
    /// <param name="actor">The authenticated caller and tenant scope.</param>
    /// <param name="manuscriptId">The manuscript identifier used for authorization.</param>
    /// <param name="fileId">The version or proofprint identifier.</param>
    /// <param name="proofprint">Whether to download a proofprint rather than a manuscript version.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<byte[]> FileAsync(Actor actor, Guid manuscriptId, Guid fileId, bool proofprint, CancellationToken ct);
}
