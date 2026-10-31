using System.Data;
using System.Text.Json;
using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.Notifications;

public interface INotificationDeliveryService
{
    Task SendNextAsync(CancellationToken ct);
}

public sealed class NotificationDeliveryService(BookFlowDbContext db, INotificationProvider provider,
    ILogger<NotificationDeliveryService> logger) : INotificationDeliveryService
{
    public async Task SendNextAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var items = await db.NotificationDeliveries.Where(x => !x.Sent && !x.DeadLettered).OrderBy(x => x.Key).ToListAsync(ct);
        var item = items.FirstOrDefault(x => x.NextAttemptAt is null || x.NextAttemptAt <= DateTimeOffset.UtcNow);
        if (item is null)
            return;
        try
        {
            var message = JsonSerializer.Deserialize<NotificationMessage>(item.Payload, EventJson.Options)
                ?? throw new PermanentFailureException("Invalid notification payload.");
            await provider.SendAsync(message, item.Key, ct);
            item.Sent = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            item.Attempts++;
            item.FailureCode = ex.GetType().Name;
            item.DeadLettered = ex is PermanentFailureException || item.Attempts >= 10;
            item.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, item.Attempts)));
            logger.LogError("Notification {Key} failed: {Code}, attempt {Attempt}, dead letter {DeadLetter}",
                item.Key, item.FailureCode, item.Attempts, item.DeadLettered);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

public sealed class NotificationSender(IServiceScopeFactory scopes, ILogger<NotificationSender> logger) : BackgroundService
{
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
