using System.Text.Json;
using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BookFlow.Notifications;

/// <summary>Resolves event recipients and schedules deduplicated notification deliveries.</summary>
/// <param name="db">The publishing workflow database context.</param>
/// <param name="transaction">The deduplicating consumer transaction service.</param>
/// <param name="config">The application configuration.</param>
public sealed class NotificationHandler(BookFlowDbContext db, IEventTransaction transaction, IConfiguration config) : IEventHandler
{
    /// <summary>The event consumer name.</summary>
    public string Consumer => "notifications";

    /// <summary>Schedules deduplicated notifications for the event's authorized recipients.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    public Task HandleAsync(DomainEvent message, CancellationToken ct)
    {
        EventLookup.RequireType(message, EventTypes.Notifications);
        return transaction.ExecuteAsync(Consumer, message, async token =>
        {
            Manuscript manuscript;
            if (message.Data.ManuscriptId is not null)
                manuscript = await EventLookup.ManuscriptAsync(db, message, token);
            else
            {
                var book = await EventLookup.BookAsync(db, message, token);
                manuscript = await db.Manuscripts.SingleAsync(x => x.Id == book.ManuscriptId, token);
            }
            var root = Hosting.Required(config, "BookFlow:PublicUrl").TrimEnd('/');
            var manuscriptLink = $"{root}/?manuscript={manuscript.Id}";
            switch (message.EventType)
            {
                case EventTypes.Assigned:
                    await EmailAsync(manuscript, manuscript.EditorId, "editor-assigned", manuscriptLink, message, token);
                    break;
                case EventTypes.Edited:
                    await EmailAsync(manuscript, manuscript.AuthorId, "manuscript-edited", manuscriptLink, message, token);
                    break;
                case EventTypes.Revised:
                    await EmailAsync(manuscript, manuscript.EditorId, "manuscript-revised", manuscriptLink, message, token);
                    break;
                case EventTypes.Proofprint:
                    if (!await db.Proofprints.Join(db.Versions, p => p.VersionId, v => v.Id, (p, v) => new { p, v })
                        .AnyAsync(x => x.p.Id == message.Data.ProofprintId && x.p.VersionId == message.Data.VersionId &&
                            x.v.ManuscriptId == manuscript.Id, token))
                        throw new PermanentFailureException("Proofprint notification references a missing PDF.");
                    await EmailAsync(manuscript, manuscript.EditorId, "proofprint-ready",
                        $"{manuscriptLink}&proofprint={message.Data.ProofprintId}", message, token);
                    break;
                case EventTypes.Approved:
                    await EmailAsync(manuscript, manuscript.AuthorId, "manuscript-approved", manuscriptLink, message, token);
                    Add(message, new NotificationMessage(manuscript.PublisherId, "book-orderable",
                        $"{root}/?book={message.Data.BookId}", PublisherApi: true));
                    break;
                case EventTypes.Available:
                    if (!Uri.TryCreate(message.Data.ListingUrl, UriKind.Absolute, out var listing) ||
                        listing.Scheme != "https" || !string.IsNullOrEmpty(listing.UserInfo) ||
                        !(config.GetSection("BookFlow:ListingHosts").Get<string[]>() ?? []).Contains(listing.Host, StringComparer.OrdinalIgnoreCase))
                        throw new PermanentFailureException("The distributor listing host is not approved.");
                    await EmailAsync(manuscript, manuscript.AuthorId, "book-available-for-sale", listing.AbsoluteUri, message, token);
                    break;
                case EventTypes.LowStock or EventTypes.Depleted:
                    await EmailAsync(manuscript, manuscript.PublisherId,
                        message.EventType == EventTypes.LowStock ? "low-stock" : "stock-depleted",
                        $"{root}/?book={message.Data.BookId}", message, token);
                    break;
            }
        }, ct);
    }

    private async Task EmailAsync(Manuscript manuscript, string? id, string template, string link, DomainEvent message, CancellationToken ct)
    {
        if (id is null)
            throw new ConflictException("Assignment has not completed; retry recipient resolution.");
        var participant = await db.Participants.SingleOrDefaultAsync(x => x.TenantId == manuscript.TenantId && x.Id == id, ct)
            ?? throw new PermanentFailureException("Notification recipient not found.");
        Add(message, new NotificationMessage(participant.Email, template, link));
    }

    private void Add(DomainEvent message, NotificationMessage notification)
    {
        var key = $"{message.Id}:{notification.Template}";
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            Key = key, Payload = JsonSerializer.Serialize(notification, EventJson.Options)
        });
    }
}
