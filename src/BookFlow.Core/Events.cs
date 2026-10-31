using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookFlow.Core;

/// <summary>Domain event names and their consumer, notification, and callback routes.</summary>
public static class EventTypes
{
    /// <summary>The event type for manuscript uploaded.</summary>
    public const string Uploaded = "ManuscriptUploaded";
    /// <summary>The event type for editor assigned.</summary>
    public const string Assigned = "EditorAssigned";
    /// <summary>The event type for manuscript edited.</summary>
    public const string Edited = "ManuscriptEdited";
    /// <summary>The event type for manuscript revised.</summary>
    public const string Revised = "ManuscriptRevised";
    /// <summary>The event type for manuscript approved.</summary>
    public const string Approved = "ManuscriptApproved";
    /// <summary>The event type for proofprint created.</summary>
    public const string Proofprint = "ProofprintCreated";
    /// <summary>The event type for print order requested.</summary>
    public const string Order = "PrintOrderRequested";
    /// <summary>The event type for books printed.</summary>
    public const string Printed = "BooksPrinted";
    /// <summary>The event type for books delivered.</summary>
    public const string Delivered = "BooksDelivered";
    /// <summary>The event type for books distributed.</summary>
    public const string Distributed = "BooksDistributed";
    /// <summary>The event type for books sold.</summary>
    public const string Sold = "BooksSold";
    /// <summary>The event type for stock updated.</summary>
    public const string Stock = "StockUpdated";
    /// <summary>The event type for low stock alert.</summary>
    public const string LowStock = "LowStockAlert";
    /// <summary>The event type for stock depleted.</summary>
    public const string Depleted = "StockDepleted";
    /// <summary>The event type for book available for sale.</summary>
    public const string Available = "BookAvailableForSale";

    /// <summary>The event types routed to each named consumer.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Subscriptions =
        new Dictionary<string, string[]>
        {
            ["editor-assignment"] = [Uploaded],
            ["proofprint-generation"] = [Uploaded, Edited, Revised],
            ["progress-tracking"] = [Uploaded, Assigned, Edited, Revised, Approved],
            ["print-demand-orders"] = [Approved, Order],
            ["inventory"] = [Printed, Delivered, Distributed, Sold, Stock, LowStock, Depleted]
        };

    /// <summary>The event types that trigger notifications.</summary>
    public static readonly string[] Notifications =
        [Assigned, Proofprint, Edited, Revised, Approved, Available, LowStock, Depleted];
    /// <summary>The event types accepted by distributor callback ingress.</summary>
    public static readonly string[] Callbacks =
        [Printed, Delivered, Distributed, Sold, Stock, LowStock, Depleted, Available];
}

/// <summary>Shared JSON serialization settings for domain events and provider contracts.</summary>
public static class EventJson
{
    /// <summary>Web JSON settings with string enums and duplicate-property rejection.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        AllowDuplicateProperties = false
    };
}

/// <summary>Versioned aggregate identifiers and ordering information carried by a domain event.</summary>
/// <param name="TenantId">The publisher tenant identifier.</param>
/// <param name="Sequence">The aggregate event sequence number.</param>
/// <param name="ManuscriptId">The associated manuscript identifier, if any.</param>
/// <param name="VersionId">The associated manuscript version identifier, if any.</param>
/// <param name="BookId">The associated book identifier, if any.</param>
/// <param name="DistributorId">The distributor's participant identifier, if any.</param>
/// <param name="PrintOrderId">The associated print order identifier, if any.</param>
/// <param name="Quantity">The number of copies involved in the operation.</param>
/// <param name="ProofprintId">The associated proofprint identifier, if any.</param>
/// <param name="ListingUrl">The publisher's book listing URL, if any.</param>
public sealed record EventData(
    Guid TenantId,
    long Sequence,
    Guid? ManuscriptId = null,
    Guid? VersionId = null,
    Guid? BookId = null,
    string? DistributorId = null,
    Guid? PrintOrderId = null,
    int? Quantity = null,
    Guid? ProofprintId = null,
    string? ListingUrl = null);

/// <summary>An Event Grid schema envelope for a committed BookFlow domain change.</summary>
/// <param name="Id">The unique domain event identifier.</param>
/// <param name="EventType">The domain event type name.</param>
/// <param name="Subject">The tenant-scoped aggregate subject path.</param>
/// <param name="EventTime">The time the domain event occurred.</param>
/// <param name="DataVersion">The event payload schema version.</param>
/// <param name="Data">The event's aggregate identifiers and ordering information.</param>
public sealed record DomainEvent(
    string Id,
    string EventType,
    string Subject,
    DateTimeOffset EventTime,
    string DataVersion,
    EventData Data)
{
    /// <summary>Creates a version-one event envelope with a new identifier and tenant-scoped subject.</summary>
    /// <param name="type">The domain event type.</param>
    /// <param name="data">The event's aggregate identifiers and sequence.</param>
    public static DomainEvent Create(string type, EventData data) =>
        new(Guid.NewGuid().ToString("N"), type,
            $"tenants/{data.TenantId}/" +
            (data.ManuscriptId is { } manuscript ? $"manuscripts/{manuscript}" : $"books/{data.BookId}"),
            DateTimeOffset.UtcNow, "1", data);

    /// <summary>Validates the event envelope, supported event type, and aggregate identity.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 ||
            DataVersion != "1" || string.IsNullOrWhiteSpace(Subject) ||
            Data is null || Data.TenantId == Guid.Empty || Data.Sequence < 1 ||
            EventTime == default ||
            !EventTypes.Subscriptions.Values.SelectMany(x => x)
                .Concat(EventTypes.Notifications).Contains(EventType))
            throw new PermanentFailureException("Invalid event envelope.");
        if (Data.ManuscriptId is null && Data.BookId is null)
            throw new PermanentFailureException("An aggregate identifier is required.");
    }
}

/// <summary>Indicates a failure that retrying the same operation cannot resolve.</summary>
/// <param name="message">The exception's failure description.</param>
public sealed class PermanentFailureException(string message) : Exception(message);
/// <summary>Indicates that the caller lacks access to an operation or resource.</summary>
public sealed class AccessDeniedException() : Exception("Access denied.");
/// <summary>Indicates a conflict with existing resource or idempotency state.</summary>
/// <param name="message">The exception's failure description.</param>
public sealed class ConflictException(string message) : Exception(message);
/// <summary>Indicates that the requested resource does not exist.</summary>
public sealed class NotFoundException() : Exception("Resource not found.");

/// <summary>Processes domain events for a named consumer.</summary>
public interface IEventHandler
{
    /// <summary>The event consumer name.</summary>
    string Consumer { get; }
    /// <summary>Processes a domain event for this consumer.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task HandleAsync(DomainEvent message, CancellationToken cancellationToken);
}

/// <summary>Stores and retrieves manuscript and proofprint file content.</summary>
public interface IBlobStore
{
    /// <summary>Writes file content without overwriting an existing blob.</summary>
    /// <param name="container">The blob container name.</param>
    /// <param name="name">The blob name within the container.</param>
    /// <param name="content">The file bytes to store.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task PutAsync(string container, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    /// <summary>Reads the bytes of a stored blob.</summary>
    /// <param name="container">The blob container name.</param>
    /// <param name="name">The blob name within the container.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<byte[]> GetAsync(string container, string name, CancellationToken cancellationToken);
}

/// <summary>Generates proofprints and submits idempotent print orders.</summary>
public interface IPrintProvider
{
    /// <summary>Generates a PDF proofprint using a stable idempotency key.</summary>
    /// <param name="manuscript">The manuscript file bytes to render.</param>
    /// <param name="idempotencyKey">The stable idempotency key for this operation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<byte[]> GenerateProofprintAsync(byte[] manuscript, string idempotencyKey, CancellationToken cancellationToken);
    /// <summary>Submits an idempotent print order and returns its provider reference.</summary>
    /// <param name="submission">The book, distributor, and copy count to print.</param>
    /// <param name="idempotencyKey">The stable idempotency key for this operation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<string> OrderAsync(PrintSubmission submission, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>A provider-neutral request to print copies of a published title.</summary>
/// <param name="BookId">The associated book identifier.</param>
/// <param name="Title">The published book title.</param>
/// <param name="Isbn">The book's thirteen-digit ISBN.</param>
/// <param name="DistributorId">The distributor's participant identifier.</param>
/// <param name="Quantity">The number of copies to print.</param>
public sealed record PrintSubmission(Guid BookId, string Title, string Isbn, string DistributorId, int Quantity);

/// <summary>Sends idempotent email or publisher API notifications.</summary>
public interface INotificationProvider
{
    /// <summary>Sends a notification using a stable idempotency key.</summary>
    /// <param name="message">The notification recipient, template, and link.</param>
    /// <param name="idempotencyKey">The stable idempotency key for this operation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task SendAsync(NotificationMessage message, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>A provider-neutral email or publisher availability notification.</summary>
/// <param name="Destination">The recipient email address or publisher API URL.</param>
/// <param name="Template">The notification template identifier.</param>
/// <param name="Link">The manuscript, proofprint, or book listing link.</param>
/// <param name="PublisherApi">Whether delivery targets the publisher API instead of email.</param>
public sealed record NotificationMessage(string Destination, string Template, string Link, bool PublisherApi = false);

/// <summary>The authenticated participant's identity, tenant, and role.</summary>
/// <param name="Id">The authenticated participant identifier.</param>
/// <param name="TenantId">The publisher tenant identifier.</param>
/// <param name="Role">The participant's authorization role.</param>
public sealed record Actor(string Id, Guid TenantId, string Role)
{
    /// <summary>Requires the specified role unless the actor is an administrator.</summary>
    /// <param name="role">The required authorization role.</param>
    public void Require(string role)
    {
        if (Role != role && Role != "Admin")
            throw new AccessDeniedException();
    }
}
