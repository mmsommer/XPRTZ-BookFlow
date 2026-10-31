using System.Data;
using System.Text.Json;
using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.Notifications;

/// <summary>Sends the next due notification and persists its delivery outcome.</summary>
public interface INotificationDeliveryService
{
    /// <summary>Leases a due notification and persists its delivery or retry outcome.</summary>
    /// <param name="ct">Cancels the operation.</param>
    Task SendNextAsync(CancellationToken ct);
}

/// <summary>Leases due notifications and records provider success, retries, or permanent failure.</summary>
/// <param name="db">The publishing workflow database context.</param>
/// <param name="provider">The notification provider used to deliver queued messages.</param>
/// <param name="logger">The logger for processing and dependency failures.</param>
public sealed class NotificationDeliveryService(BookFlowDbContext db, INotificationProvider provider,
    ILogger<NotificationDeliveryService> logger) : INotificationDeliveryService
{
    /// <summary>Leases a due notification and persists its delivery or retry outcome.</summary>
    /// <param name="ct">Cancels the operation.</param>
    public async Task SendNextAsync(CancellationToken ct)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        NotificationDelivery? item;
        await using (var claim = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var now = DateTimeOffset.UtcNow;
            item = await db.NotificationDeliveries.Where(x => !x.Sent && !x.DeadLettered &&
                    x.LeaseUntilUtcTicks <= now.UtcTicks &&
                    (x.NextAttemptAtUtcTicks == null || x.NextAttemptAtUtcTicks <= now.UtcTicks))
                .OrderBy(x => x.Key).FirstOrDefaultAsync(ct);
            if (item is null)
            {
                await claim.CommitAsync(ct);
                return;
            }
            item.LeaseId = leaseId;
            item.LeaseUntilUtcTicks = now.AddMinutes(2).UtcTicks;
            await db.SaveChangesAsync(ct);
            await claim.CommitAsync(ct);
        }

        Exception? failure = null;
        try
        {
            var message = JsonSerializer.Deserialize<NotificationMessage>(item.Payload, EventJson.Options)
                ?? throw new PermanentFailureException("Invalid notification payload.");
            await provider.SendAsync(message, item.Key, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failure = ex;
        }

        await using var completion = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.Entry(item).ReloadAsync(ct);
        if (item.LeaseId != leaseId)
        {
            await completion.CommitAsync(ct);
            return;
        }
        if (failure is null)
            item.Sent = true;
        else
        {
            item.Attempts++;
            item.FailureCode = failure.GetType().Name;
            item.DeadLettered = failure is PermanentFailureException || item.Attempts >= 10;
            var nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, item.Attempts)));
            item.NextAttemptAt = nextAttemptAt;
            item.NextAttemptAtUtcTicks = nextAttemptAt.UtcTicks;
            logger.LogError("Notification {Key} failed: {Code}, attempt {Attempt}, dead letter {DeadLetter}",
                item.Key, item.FailureCode, item.Attempts, item.DeadLettered);
        }
        item.LeaseId = null;
        item.LeaseUntilUtcTicks = 0;
        await db.SaveChangesAsync(ct);
        await completion.CommitAsync(ct);
    }
}

/// <summary>Runs queued notification delivery in background service scopes.</summary>
/// <param name="scopes">The factory for per-operation service scopes.</param>
/// <param name="logger">The logger for processing and dependency failures.</param>
public sealed class NotificationSender(IServiceScopeFactory scopes, ILogger<NotificationSender> logger) : BackgroundService
{
    /// <summary>Commits the consumer action and inbox acknowledgement once per event.</summary>
    /// <param name="stoppingToken">Cancels processing when the background service stops.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<INotificationDeliveryService>().SendNextAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Notification dispatch failed");
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
}
