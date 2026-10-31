using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Notifications;

var builder = WebApplication.CreateBuilder(args);
builder.AddBookFlow();
builder.Services.AddProvider(builder.Configuration);
builder.Services.AddScoped<IEventHandler, NotificationHandler>();
builder.Services.AddScoped<INotificationAcceptance, NotificationAcceptance>();
builder.Services.AddScoped<INotificationDeliveryService, NotificationDeliveryService>();
builder.Services.AddHostedService(services => new LocalEventWorker(
    services.GetRequiredService<IServiceScopeFactory>(), "notifications", services.GetRequiredService<ILogger<LocalEventWorker>>()));
builder.Services.AddHostedService<NotificationSender>();
var app = builder.Build();
await app.UseBookFlowAsync();
app.MapPost("/notifications", async (DomainEvent[] messages, INotificationAcceptance service, CancellationToken ct) =>
{
    await service.AcceptAsync(messages, ct);
    return TypedResults.Accepted((string?)null);
}).RequireAuthorization("EventGrid").WithSummary("Durably accept authenticated notification events");
await app.RunAsync();
