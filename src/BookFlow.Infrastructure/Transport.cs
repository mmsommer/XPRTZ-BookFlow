using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using BookFlow.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookFlow.Infrastructure;

/// <summary>Publishes committed domain events to their transport.</summary>
public interface IEventPublisher
{
    /// <summary>Publishes a committed domain event.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task PublishAsync(DomainEvent message, CancellationToken ct);
}

/// <summary>Publishes domain events to Azure Event Grid.</summary>
/// <param name="client">The Azure Event Grid publishing client.</param>
public sealed class AzureEventPublisher(EventGridPublisherClient client) : IEventPublisher
{
    /// <summary>Publishes a committed domain event.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    public async Task PublishAsync(DomainEvent message, CancellationToken ct)
    {
        var envelope = new EventGridEvent(message.Subject, message.EventType, message.DataVersion,
            BinaryData.FromObjectAsJson(message.Data, EventJson.Options))
        {
            Id = message.Id,
            EventTime = message.EventTime
        };
        await client.SendEventAsync(envelope, ct);
    }
}

/// <summary>Queues local consumer deliveries and forwards notification events.</summary>
/// <param name="db">The publishing workflow database context.</param>
/// <param name="client">The HTTP client for provider requests or local notification forwarding.</param>
public sealed class LocalEventPublisher(BookFlowDbContext db, HttpClient client) : IEventPublisher, IDisposable
{
    /// <summary>Disposes the HTTP client used for local event forwarding.</summary>
    public void Dispose() => client.Dispose();

    /// <summary>Publishes a committed domain event.</summary>
    /// <param name="message">The domain event to process.</param>
    /// <param name="ct">Cancels the operation.</param>
    public async Task PublishAsync(DomainEvent message, CancellationToken ct)
    {
        foreach (var (consumer, types) in EventTypes.Subscriptions)
        {
            if (types.Contains(message.EventType) &&
                !await db.LocalDeliveries.AnyAsync(x => x.Consumer == consumer && x.EventId == message.Id, ct))
                db.LocalDeliveries.Add(new LocalDelivery
                {
                    Consumer = consumer, EventId = message.Id,
                    Payload = JsonSerializer.Serialize(message, EventJson.Options)
                });
        }
        await db.SaveChangesAsync(ct);
        if (EventTypes.Notifications.Contains(message.EventType))
        {
            using var response = await client.PostAsJsonAsync("events/notifications", new[] { message }, EventJson.Options, ct);
            if ((int)response.StatusCode is >= 400 and < 500 && (int)response.StatusCode is not (408 or 429))
                throw new PermanentFailureException($"Notification ingress rejected event with status {(int)response.StatusCode}.");
            response.EnsureSuccessStatusCode();
        }
    }
}

/// <summary>Publishes leased outbox messages and persists retry or dead-letter state.</summary>
/// <param name="scopes">The factory for per-operation service scopes.</param>
/// <param name="logger">The logger for processing and dependency failures.</param>
public sealed class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> logger) : BackgroundService
{
    /// <summary>Processes queued events until background service cancellation.</summary>
    /// <param name="stoppingToken">Cancels processing when the background service stops.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BookFlowDbContext>();
                var leaseId = Guid.NewGuid().ToString("N");
                OutboxMessage? item;
                await using (var claim = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken))
                {
                    var now = DateTimeOffset.UtcNow;
                    item = await db.Outbox.Where(x => !x.Published && !x.DeadLettered &&
                            x.LeaseUntilUtcTicks < now.UtcTicks &&
                            (x.NextAttemptAtUtcTicks == null || x.NextAttemptAtUtcTicks <= now.UtcTicks))
                        .OrderBy(x => x.Id).FirstOrDefaultAsync(stoppingToken);
                    if (item is not null)
                    {
                        item.LeaseId = leaseId;
                        item.LeaseUntilUtcTicks = now.AddMinutes(2).UtcTicks;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    await claim.CommitAsync(stoppingToken);
                }
                if (item is not null)
                {
                    try
                    {
                        var message = JsonSerializer.Deserialize<DomainEvent>(item.Payload, EventJson.Options)
                            ?? throw new PermanentFailureException("Empty outbox payload.");
                        await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(message, stoppingToken);
                        item.Published = true;
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        item.Attempts++;
                        item.FailureCode = ex.GetType().Name;
                        item.DeadLettered = ex is PermanentFailureException || item.Attempts >= 10;
                        var nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, item.Attempts)));
                        item.NextAttemptAt = nextAttemptAt;
                        item.NextAttemptAtUtcTicks = nextAttemptAt.UtcTicks;
                        logger.LogError("Outbox failure for {EventId}, attempt {Attempt}, code {Code}, dead letter {DeadLetter}",
                            item.Id, item.Attempts, item.FailureCode, item.DeadLettered);
                    }
                    item.LeaseId = null;
                    item.LeaseUntilUtcTicks = 0;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Outbox polling failed");
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
}

/// <summary>Processes locally queued deliveries for one consumer.</summary>
/// <param name="scopes">The factory for per-operation service scopes.</param>
/// <param name="consumer">The consumer name used for routing and deduplication.</param>
/// <param name="logger">The logger for processing and dependency failures.</param>
public sealed class LocalEventWorker(IServiceScopeFactory scopes, string consumer, ILogger<LocalEventWorker> logger) : BackgroundService
{
    /// <summary>Processes queued events until background service cancellation.</summary>
    /// <param name="stoppingToken">Cancels processing when the background service stops.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string? eventId = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BookFlowDbContext>();
                var now = DateTimeOffset.UtcNow;
                var item = await db.LocalDeliveries.Where(x => x.Consumer == consumer && !x.Completed &&
                        !x.DeadLettered && (x.NextAttemptAtUtcTicks == null || x.NextAttemptAtUtcTicks <= now.UtcTicks))
                    .OrderBy(x => x.EventId).FirstOrDefaultAsync(stoppingToken);
                if (item is not null)
                {
                    eventId = item.EventId;
                    var message = JsonSerializer.Deserialize<DomainEvent>(item.Payload, EventJson.Options)
                        ?? throw new PermanentFailureException("Empty delivery payload.");
                    await scope.ServiceProvider.GetRequiredService<IEventHandler>().HandleAsync(message, stoppingToken);
                    item.Completed = true;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Consumer {Consumer} failed for {EventId}: {Code}", consumer, eventId, ex.GetType().Name);
                if (eventId is not null)
                {
                    await using var failureScope = scopes.CreateAsyncScope();
                    var db = failureScope.ServiceProvider.GetRequiredService<BookFlowDbContext>();
                    var item = await db.LocalDeliveries.SingleAsync(x => x.Consumer == consumer && x.EventId == eventId, stoppingToken);
                    item.Attempts++;
                    item.FailureCode = ex.GetType().Name;
                    item.DeadLettered = ex is PermanentFailureException || item.Attempts >= 10;
                    var nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, item.Attempts)));
                    item.NextAttemptAt = nextAttemptAt;
                    item.NextAttemptAtUtcTicks = nextAttemptAt.UtcTicks;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
}

/// <summary>Receives and processes Azure Service Bus events for one consumer subscription.</summary>
/// <param name="client">The Azure Service Bus client.</param>
/// <param name="scopes">The factory for per-operation service scopes.</param>
/// <param name="topic">The Service Bus topic name.</param>
/// <param name="consumer">The consumer name used for routing and deduplication.</param>
/// <param name="logger">The logger for processing and dependency failures.</param>
public sealed class AzureEventWorker(ServiceBusClient client, IServiceScopeFactory scopes,
    string topic, string consumer, ILogger<AzureEventWorker> logger) : BackgroundService
{
    /// <summary>Processes queued events until background service cancellation.</summary>
    /// <param name="stoppingToken">Cancels processing when the background service stops.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = client.CreateProcessor(topic, consumer, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false, MaxConcurrentCalls = 1,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10)
        });
        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var body = args.Message.Body.ToString();
                using var json = JsonDocument.Parse(body);
                var events = json.RootElement.ValueKind == JsonValueKind.Array
                    ? JsonSerializer.Deserialize<DomainEvent[]>(body, EventJson.Options)
                    : [JsonSerializer.Deserialize<DomainEvent>(body, EventJson.Options)
                        ?? throw new PermanentFailureException("Empty event body.")];
                if (events is null || events.Length != 1)
                    throw new PermanentFailureException("Expected one Event Grid event per Service Bus message.");
                var message = events[0];
                if (!args.Message.ApplicationProperties.TryGetValue("eventType", out var eventType) ||
                    !Equals(eventType, message.EventType) || args.Message.MessageId != message.Id)
                    throw new PermanentFailureException("Transport attributes do not match the envelope.");
                await scope.ServiceProvider.GetRequiredService<IEventHandler>().HandleAsync(message, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception ex) when (ex is PermanentFailureException or JsonException)
            {
                logger.LogError("Dead lettering {EventId} for {Consumer}: {Code}", args.Message.MessageId, consumer, ex.GetType().Name);
                await args.DeadLetterMessageAsync(args.Message, "InvalidDomainEvent", ex.GetType().Name, args.CancellationToken);
            }
        };
        processor.ProcessErrorAsync += args =>
        {
            logger.LogError("Service Bus error for {Consumer}: {Code}", consumer, args.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }
}
