using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookFlow.Core;

public static class EventTypes
{
    public const string Uploaded = "ManuscriptUploaded";
    public const string Assigned = "EditorAssigned";
    public const string Edited = "ManuscriptEdited";
    public const string Revised = "ManuscriptRevised";
    public const string Approved = "ManuscriptApproved";
    public const string Proofprint = "ProofprintCreated";
    public const string Order = "PrintOrderRequested";
    public const string Printed = "BooksPrinted";
    public const string Delivered = "BooksDelivered";
    public const string Distributed = "BooksDistributed";
    public const string Sold = "BooksSold";
    public const string Stock = "StockUpdated";
    public const string LowStock = "LowStockAlert";
    public const string Depleted = "StockDepleted";
    public const string Available = "BookAvailableForSale";

    public static readonly IReadOnlyDictionary<string, string[]> Subscriptions =
        new Dictionary<string, string[]>
        {
            ["editor-assignment"] = [Uploaded],
            ["proofprint-generation"] = [Uploaded, Edited, Revised],
            ["progress-tracking"] = [Uploaded, Assigned, Edited, Revised, Approved],
            ["print-demand-orders"] = [Approved, Order],
            ["inventory"] = [Printed, Delivered, Distributed, Sold, Stock, LowStock, Depleted]
        };

    public static readonly string[] Notifications =
        [Assigned, Proofprint, Edited, Revised, Approved, Available, LowStock, Depleted];
    public static readonly string[] Callbacks =
        [Printed, Delivered, Distributed, Sold, Stock, LowStock, Depleted, Available];
}

public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        AllowDuplicateProperties = false
    };
}

/// <summary>Versioned aggregate identifiers and ordering information carried by a domain event.</summary>
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
public sealed record DomainEvent(
    string Id,
    string EventType,
    string Subject,
    DateTimeOffset EventTime,
    string DataVersion,
    EventData Data)
{
    public static DomainEvent Create(string type, EventData data) =>
        new(Guid.NewGuid().ToString("N"), type,
            $"tenants/{data.TenantId}/" +
            (data.ManuscriptId is { } manuscript ? $"manuscripts/{manuscript}" : $"books/{data.BookId}"),
            DateTimeOffset.UtcNow, "1", data);

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

public sealed class PermanentFailureException(string message) : Exception(message);
public sealed class AccessDeniedException() : Exception("Access denied.");
public sealed class ConflictException(string message) : Exception(message);
public sealed class NotFoundException() : Exception("Resource not found.");

public interface IEventHandler
{
    string Consumer { get; }
    Task HandleAsync(DomainEvent message, CancellationToken cancellationToken);
}

public interface IBlobStore
{
    Task PutAsync(string container, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    Task<byte[]> GetAsync(string container, string name, CancellationToken cancellationToken);
}

public interface IPrintProvider
{
    Task<byte[]> GenerateProofprintAsync(byte[] manuscript, string idempotencyKey, CancellationToken cancellationToken);
    Task<string> OrderAsync(PrintSubmission submission, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>A provider-neutral request to print copies of a published title.</summary>
public sealed record PrintSubmission(Guid BookId, string Title, string Isbn, string DistributorId, int Quantity);

public interface INotificationProvider
{
    Task SendAsync(NotificationMessage message, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>A provider-neutral email or publisher availability notification.</summary>
public sealed record NotificationMessage(string Destination, string Template, string Link, bool PublisherApi = false);

public sealed record Actor(string Id, Guid TenantId, string Role)
{
    public void Require(string role)
    {
        if (Role != role && Role != "Admin")
            throw new AccessDeniedException();
    }
}
