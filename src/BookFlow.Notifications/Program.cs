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
}).RequireAuthorization("EventGrid").WithSummary("Durably accept authenticated notification events")
    .Describe("AcceptPrivateNotifications", "Notifications", "Private endpoint requiring the EventGrid application role. Accepts 1 through 100 version-1 DomainEvents of types EditorAssigned, ProofprintCreated, ManuscriptEdited, ManuscriptRevised, ManuscriptApproved, BookAvailableForSale, LowStockAlert or StockDepleted. Persists a receipt before responding; recipient resolution and external delivery happen asynchronously with retries. Identical event-ID retries are ignored, conflicting payloads return 409. Proofprint events require manuscript/version/proofprint IDs; approval requires manuscript/book IDs; availability and stock alerts require a book ID.", 400, 401, 403, 409, 500);
await app.RunAsync();
