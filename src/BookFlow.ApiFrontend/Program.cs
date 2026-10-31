using System.Text.Json;
using System.Threading.RateLimiting;
using BookFlow.ApiFrontend;
using BookFlow.Core;
using BookFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddBookFlow(outbox: true);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16000000);
builder.Services.AddScoped<ICallbackService, CallbackService>();
builder.Services.AddScoped<IFrontendService, FrontendService>();
builder.Services.AddHttpClient("webapp", client =>
{
    client.BaseAddress = new Uri(Hosting.Required(builder.Configuration, "BookFlow:WebAppUrl"));
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient("notifications", client =>
{
    client.BaseAddress = new Uri(Hosting.Required(builder.Configuration, "BookFlow:NotificationsUrl"));
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("ingress", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("oid")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
});
var app = builder.Build();
await app.UseBookFlowAsync();
app.UseRateLimiter();
app.MapPost("/callbacks", async (DomainEvent message, HttpContext context, ICallbackService service, CancellationToken ct) =>
{
    await service.AcceptAsync(Hosting.GetActor(context.User), message, ct);
    return TypedResults.Accepted((string?)null);
}).RequireAuthorization().RequireRateLimiting("ingress").WithSummary("Validate and durably accept a provider or distributor callback");
app.MapPost("/events/notifications", async Task<IResult> (
    JsonElement[] events, HttpContext context, IFrontendService service, CancellationToken ct) =>
{
    if (events.Length is < 1 or > 100)
        throw new ArgumentException("Invalid Event Grid batch size.");
    if (events.Length == 1 && events[0].TryGetProperty("eventType", out var type) &&
        type.GetString() == "Microsoft.EventGrid.SubscriptionValidationEvent")
    {
        if (!events[0].TryGetProperty("data", out var data) ||
            !data.TryGetProperty("validationCode", out var code) || string.IsNullOrWhiteSpace(code.GetString()))
            throw new ArgumentException("Missing subscription validation code.");
        return TypedResults.Ok(new { validationResponse = code.GetString() });
    }
    await service.ForwardNotificationsAsync(events, context.Request.Headers.Authorization.ToString(), ct);
    return TypedResults.Accepted((string?)null);
}).RequireAuthorization("EventGrid").RequireRateLimiting("ingress").WithSummary("Validate Event Grid subscriptions and forward notifications privately");
app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"],
    async (string? path, HttpContext context, IFrontendService service, CancellationToken ct) =>
    {
        using var response = await service.ProxyAsync(context.Request, $"api/{path}", ct);
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } contentType)
            context.Response.ContentType = contentType.ToString();
        if (response.Headers.Location is { } location)
            context.Response.Headers.Location = location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
        if (response.Content.Headers.ContentDisposition is { } disposition)
            context.Response.Headers.ContentDisposition = disposition.ToString();
        context.Response.Headers.CacheControl = "no-store";
        await response.Content.CopyToAsync(context.Response.Body, ct);
    }).RequireAuthorization().RequireRateLimiting("ingress").WithSummary("Proxy authenticated application operations");
app.MapGet("/", async (HttpContext context, IFrontendService service, CancellationToken ct) =>
{
    using var response = await service.ProxyAsync(context.Request, "", ct);
    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
    await response.Content.CopyToAsync(context.Response.Body, ct);
}).AllowAnonymous().WithSummary("Open the BookFlow web client");
await app.RunAsync();
