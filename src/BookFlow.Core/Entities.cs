namespace BookFlow.Core;

/// <summary>Configures inventory alert thresholds.</summary>
public sealed class InventoryOptions
{
    /// <summary>The available-copy threshold at or below which low-stock alerts are raised.</summary>
    public int LowStockThreshold { get; set; } = 10;
}

/// <summary>Stages of the manuscript editing and approval workflow.</summary>
public enum ManuscriptStatus
{
    /// <summary>The manuscript has been uploaded and awaits editor assignment.</summary>
    Submitted,
    /// <summary>An editor has been assigned to the manuscript.</summary>
    AssignedForEditing,
    /// <summary>Editor changes await the author's response.</summary>
    AwaitingAuthorResponse,
    /// <summary>An author revision awaits editor review.</summary>
    AwaitingEditorReview,
    /// <summary>The manuscript has been approved for publication.</summary>
    Approved
}

/// <summary>A registered participant and role within a publisher tenant.</summary>
public sealed class Participant
{
    /// <summary>The participant identifier within the tenant.</summary>
    public string Id { get; set; } = "";
    /// <summary>The publisher tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>The participant's display name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The participant's email address.</summary>
    public string Email { get; set; } = "";
    /// <summary>The participant's authorization role.</summary>
    public string Role { get; set; } = "";
    /// <summary>Whether the participant is active.</summary>
    public bool Active { get; set; } = true;
}

/// <summary>A submitted manuscript with authoritative and projected workflow state.</summary>
public sealed class Manuscript
{
    /// <summary>The manuscript identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>The publisher tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>The manuscript author's participant identifier.</summary>
    public string AuthorId { get; set; } = "";
    /// <summary>The publisher's participant identifier.</summary>
    public string PublisherId { get; set; } = "";
    /// <summary>The assigned editor's participant identifier, if any.</summary>
    public string? EditorId { get; set; }
    /// <summary>The manuscript title.</summary>
    public string Title { get; set; } = "";
    /// <summary>The authoritative workflow status.</summary>
    public ManuscriptStatus Status { get; set; }
    /// <summary>The status projected from contiguous workflow history.</summary>
    public ManuscriptStatus ProgressStatus { get; set; }
    /// <summary>The aggregate event sequence number.</summary>
    public long Sequence { get; set; }
    /// <summary>The last contiguous sequence applied to the progress projection.</summary>
    public long ProgressSequence { get; set; }
    /// <summary>The time the manuscript was submitted.</summary>
    public DateTimeOffset SubmittedAt { get; set; }
}

/// <summary>An immutable manuscript file version and its parent revision.</summary>
public sealed class ManuscriptVersion
{
    /// <summary>The manuscript version identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>The associated manuscript identifier.</summary>
    public Guid ManuscriptId { get; set; }
    /// <summary>The manuscript version number.</summary>
    public int Number { get; set; }
    /// <summary>The parent manuscript version identifier, if any.</summary>
    public Guid? ParentId { get; set; }
    /// <summary>The stored file's blob name.</summary>
    public string BlobName { get; set; } = "";
    /// <summary>The time this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Metadata associated with a manuscript.</summary>
public sealed class ManuscriptMetadata
{
    /// <summary>The associated manuscript identifier.</summary>
    public Guid ManuscriptId { get; set; }
}

/// <summary>A text replacement proposed by an editor for a manuscript version.</summary>
public sealed class ManuscriptHighlight
{
    /// <summary>The editor highlight identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>The associated manuscript version identifier.</summary>
    public Guid VersionId { get; set; }
    /// <summary>The inclusive start offset of the highlighted text.</summary>
    public int StartOffset { get; set; }
    /// <summary>The end offset of the highlighted text.</summary>
    public int EndOffset { get; set; }
    /// <summary>The text being replaced.</summary>
    public string OriginalText { get; set; } = "";
    /// <summary>The editor's replacement text.</summary>
    public string EditedText { get; set; } = "";
}

/// <summary>An author's feedback attached to a manuscript version.</summary>
public sealed class AuthorFeedback
{
    /// <summary>The author feedback identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>The associated manuscript version identifier.</summary>
    public Guid VersionId { get; set; }
    /// <summary>The manuscript author's participant identifier.</summary>
    public string AuthorId { get; set; } = "";
    /// <summary>The author's feedback text.</summary>
    public string Comment { get; set; } = "";
    /// <summary>The time the feedback was submitted.</summary>
    public DateTimeOffset SubmittedAt { get; set; }
}

/// <summary>A sequenced manuscript workflow event retained for progress projection.</summary>
public sealed class StatusHistory
{
    /// <summary>The domain event identifier.</summary>
    public string EventId { get; set; } = "";
    /// <summary>The associated manuscript identifier.</summary>
    public Guid ManuscriptId { get; set; }
    /// <summary>The aggregate event sequence number.</summary>
    public long Sequence { get; set; }
    /// <summary>The manuscript status recorded by this event.</summary>
    public ManuscriptStatus Status { get; set; }
    /// <summary>The time of the workflow status change.</summary>
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>A generated proofprint file associated with a manuscript version.</summary>
public sealed class Proofprint
{
    /// <summary>The proofprint identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>The associated manuscript version identifier.</summary>
    public Guid VersionId { get; set; }
    /// <summary>The stored file's blob name.</summary>
    public string BlobName { get; set; } = "";
    /// <summary>The time this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An approved manuscript's published title and orderability.</summary>
public sealed class Book
{
    /// <summary>The published book identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>The associated manuscript identifier.</summary>
    public Guid ManuscriptId { get; set; }
    /// <summary>The publisher tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>The publisher's participant identifier.</summary>
    public string PublisherId { get; set; } = "";
    /// <summary>The published book title.</summary>
    public string Title { get; set; } = "";
    /// <summary>The book's thirteen-digit ISBN.</summary>
    public string Isbn { get; set; } = "";
    /// <summary>Whether additional copies may be ordered.</summary>
    public bool Orderable { get; set; }
}

/// <summary>Stages of a print order from request through delivery.</summary>
public enum PrintOrderStatus
{
    /// <summary>The print order has been requested.</summary>
    Requested,
    /// <summary>The print order has been submitted to the provider.</summary>
    Submitted,
    /// <summary>The ordered copies have been printed.</summary>
    Printed,
    /// <summary>The ordered copies have been delivered.</summary>
    Delivered
}

/// <summary>A request to print copies for a distributor with provider tracking details.</summary>
public sealed class PrintOrder
{
    /// <summary>The print order identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>The associated book identifier.</summary>
    public Guid BookId { get; set; }
    /// <summary>The distributor's participant identifier.</summary>
    public string DistributorId { get; set; } = "";
    /// <summary>The number of copies ordered.</summary>
    public int Quantity { get; set; }
    /// <summary>The print order's fulfillment status.</summary>
    public PrintOrderStatus Status { get; set; }
    /// <summary>The print provider's order reference, if assigned.</summary>
    public string? ExternalReference { get; set; }
    /// <summary>The time this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Projected inventory totals for one book and distributor.</summary>
public sealed class StockRecord
{
    /// <summary>The associated book identifier.</summary>
    public Guid BookId { get; set; }
    /// <summary>The distributor's participant identifier.</summary>
    public string DistributorId { get; set; } = "";
    /// <summary>The number of copies currently available.</summary>
    public int AvailableQuantity { get; set; }
    /// <summary>The number of copies recorded as printed.</summary>
    public int PrintedQuantity { get; set; }
    /// <summary>The number of copies recorded as delivered.</summary>
    public int DeliveredQuantity { get; set; }
    /// <summary>The number of copies recorded as distributed.</summary>
    public int DistributedQuantity { get; set; }
    /// <summary>The number of copies recorded as sold.</summary>
    public int SoldQuantity { get; set; }
    /// <summary>The aggregate event sequence number.</summary>
    public long Sequence { get; set; }
    /// <summary>The latest inventory alert type, if any.</summary>
    public string? Alert { get; set; }
    /// <summary>The time the stock projection was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A sequenced inventory event used to rebuild distributor stock.</summary>
public sealed class InventoryLedgerEntry
{
    /// <summary>The domain event identifier.</summary>
    public string EventId { get; set; } = "";
    /// <summary>The associated book identifier.</summary>
    public Guid BookId { get; set; }
    /// <summary>The distributor's participant identifier.</summary>
    public string DistributorId { get; set; } = "";
    /// <summary>The domain event type name.</summary>
    public string EventType { get; set; } = "";
    /// <summary>The aggregate event sequence number.</summary>
    public long Sequence { get; set; }
    /// <summary>The number of copies involved in the operation.</summary>
    public int Quantity { get; set; }
    /// <summary>The time the inventory event was recorded.</summary>
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>A committed domain event awaiting publication, with retry and lease state.</summary>
public sealed class OutboxMessage
{
    /// <summary>The domain event identifier used for publication deduplication.</summary>
    public string Id { get; set; } = "";
    /// <summary>The serialized event or notification payload.</summary>
    public string Payload { get; set; } = "";
    /// <summary>The time this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Whether the outbox event has been published.</summary>
    public bool Published { get; set; }
    /// <summary>The number of failed publication attempts.</summary>
    public int Attempts { get; set; }
    /// <summary>The next scheduled retry time, if any.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>The next scheduled retry time expressed as UTC ticks, if any.</summary>
    public long? NextAttemptAtUtcTicks { get; set; }
    /// <summary>The recorded failure classification, if any.</summary>
    public string? FailureCode { get; set; }
    /// <summary>Whether automatic delivery or publication has been abandoned.</summary>
    public bool DeadLettered { get; set; }
    /// <summary>The current delivery or publication lease identifier, if any.</summary>
    public string? LeaseId { get; set; }
    /// <summary>The lease expiry time expressed as UTC ticks.</summary>
    public long LeaseUntilUtcTicks { get; set; }
}

/// <summary>A consumer's durable acknowledgement of a processed domain event.</summary>
public sealed class InboxMessage
{
    /// <summary>The event consumer name.</summary>
    public string Consumer { get; set; } = "";
    /// <summary>The domain event identifier.</summary>
    public string EventId { get; set; } = "";
    /// <summary>The time the consumer acknowledged the event.</summary>
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>A locally queued event delivery with completion and retry state.</summary>
public sealed class LocalDelivery
{
    /// <summary>The event consumer name.</summary>
    public string Consumer { get; set; } = "";
    /// <summary>The domain event identifier.</summary>
    public string EventId { get; set; } = "";
    /// <summary>The serialized event or notification payload.</summary>
    public string Payload { get; set; } = "";
    /// <summary>The number of failed delivery attempts.</summary>
    public int Attempts { get; set; }
    /// <summary>Whether the local event delivery completed.</summary>
    public bool Completed { get; set; }
    /// <summary>Whether automatic delivery or publication has been abandoned.</summary>
    public bool DeadLettered { get; set; }
    /// <summary>The recorded failure classification, if any.</summary>
    public string? FailureCode { get; set; }
    /// <summary>The next scheduled retry time, if any.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>The next scheduled retry time expressed as UTC ticks, if any.</summary>
    public long? NextAttemptAtUtcTicks { get; set; }
}

/// <summary>The request fingerprint and resource returned for an idempotency key.</summary>
public sealed class IdempotencyRecord
{
    /// <summary>The stable deduplication key for this operation.</summary>
    public string Key { get; set; } = "";
    /// <summary>The fingerprint of the original idempotent request payload.</summary>
    public string RequestHash { get; set; } = "";
    /// <summary>The resource returned by the original idempotent request.</summary>
    public Guid ResourceId { get; set; }
}

/// <summary>A recoverable reservation for an immutable manuscript blob upload.</summary>
public sealed class UploadReservation
{
    /// <summary>The reservation identifier derived from the upload request key.</summary>
    public Guid Id { get; set; }
    /// <summary>The stored file's blob name.</summary>
    public string BlobName { get; set; } = "";
    /// <summary>The upload reservation state.</summary>
    public string State { get; set; } = "Pending";
    /// <summary>The time this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A queued notification with delivery, retry, and lease state.</summary>
public sealed class NotificationDelivery
{
    /// <summary>The stable deduplication key for this operation.</summary>
    public string Key { get; set; } = "";
    /// <summary>The serialized event or notification payload.</summary>
    public string Payload { get; set; } = "";
    /// <summary>Whether the notification was sent successfully.</summary>
    public bool Sent { get; set; }
    /// <summary>The number of failed notification attempts.</summary>
    public int Attempts { get; set; }
    /// <summary>Whether automatic delivery or publication has been abandoned.</summary>
    public bool DeadLettered { get; set; }
    /// <summary>The recorded failure classification, if any.</summary>
    public string? FailureCode { get; set; }
    /// <summary>The next scheduled retry time, if any.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>The next scheduled retry time expressed as UTC ticks, if any.</summary>
    public long? NextAttemptAtUtcTicks { get; set; }
    /// <summary>The current delivery or publication lease identifier, if any.</summary>
    public string? LeaseId { get; set; }
    /// <summary>The lease expiry time expressed as UTC ticks.</summary>
    public long LeaseUntilUtcTicks { get; set; }
}
