namespace BookFlow.Core;

public sealed class InventoryOptions
{
    public int LowStockThreshold { get; set; } = 10;
}

public enum ManuscriptStatus
{
    Submitted,
    AssignedForEditing,
    AwaitingAuthorResponse,
    AwaitingEditorReview,
    Approved
}

public sealed class Participant
{
    public string Id { get; set; } = "";
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class Manuscript
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string AuthorId { get; set; } = "";
    public string PublisherId { get; set; } = "";
    public string? EditorId { get; set; }
    public string Title { get; set; } = "";
    public ManuscriptStatus Status { get; set; }
    public ManuscriptStatus ProgressStatus { get; set; }
    public long Sequence { get; set; }
    public long ProgressSequence { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
}

public sealed class ManuscriptVersion
{
    public Guid Id { get; set; }
    public Guid ManuscriptId { get; set; }
    public int Number { get; set; }
    public Guid? ParentId { get; set; }
    public string BlobName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ManuscriptMetadata
{
    public Guid ManuscriptId { get; set; }
}

public sealed class ManuscriptHighlight
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string OriginalText { get; set; } = "";
    public string EditedText { get; set; } = "";
}

public sealed class AuthorFeedback
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public string AuthorId { get; set; } = "";
    public string Comment { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
}

public sealed class StatusHistory
{
    public string EventId { get; set; } = "";
    public Guid ManuscriptId { get; set; }
    public long Sequence { get; set; }
    public ManuscriptStatus Status { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class Proofprint
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public string BlobName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Book
{
    public Guid Id { get; set; }
    public Guid ManuscriptId { get; set; }
    public Guid TenantId { get; set; }
    public string PublisherId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Isbn { get; set; } = "";
    public bool Orderable { get; set; }
}

public enum PrintOrderStatus { Requested, Submitted, Printed, Delivered }

public sealed class PrintOrder
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }
    public string DistributorId { get; set; } = "";
    public int Quantity { get; set; }
    public PrintOrderStatus Status { get; set; }
    public string? ExternalReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StockRecord
{
    public Guid BookId { get; set; }
    public string DistributorId { get; set; } = "";
    public int AvailableQuantity { get; set; }
    public int PrintedQuantity { get; set; }
    public int DeliveredQuantity { get; set; }
    public int DistributedQuantity { get; set; }
    public int SoldQuantity { get; set; }
    public long Sequence { get; set; }
    public string? Alert { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class InventoryLedgerEntry
{
    public string EventId { get; set; } = "";
    public Guid BookId { get; set; }
    public string DistributorId { get; set; } = "";
    public string EventType { get; set; } = "";
    public long Sequence { get; set; }
    public int Quantity { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class OutboxMessage
{
    public string Id { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Published { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public long? NextAttemptAtUtcTicks { get; set; }
    public string? FailureCode { get; set; }
    public bool DeadLettered { get; set; }
    public string? LeaseId { get; set; }
    public long LeaseUntilUtcTicks { get; set; }
}

public sealed class InboxMessage
{
    public string Consumer { get; set; } = "";
    public string EventId { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class LocalDelivery
{
    public string Consumer { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Payload { get; set; } = "";
    public int Attempts { get; set; }
    public bool Completed { get; set; }
    public bool DeadLettered { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public long? NextAttemptAtUtcTicks { get; set; }
}

public sealed class IdempotencyRecord
{
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid ResourceId { get; set; }
}

public sealed class UploadReservation
{
    public Guid Id { get; set; }
    public string BlobName { get; set; } = "";
    public string State { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NotificationDelivery
{
    public string Key { get; set; } = "";
    public string Payload { get; set; } = "";
    public bool Sent { get; set; }
    public int Attempts { get; set; }
    public bool DeadLettered { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public long? NextAttemptAtUtcTicks { get; set; }
}
