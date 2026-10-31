using System.Text.Json;
using System.Threading.RateLimiting;
using BookFlow.ApiFrontend;
using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

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
builder.Services.Configure<OpenApiOptions>("v1", options =>
{
    options.AddOperationTransformer(async (operation, context, ct) =>
    {
        if (context.Description.RelativePath == "events/notifications")
        {
            var domainEventSchema = await context.GetOrCreateSchemaAsync(typeof(DomainEvent), cancellationToken: ct);
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Description = "An array of 1 through 100 domain notification events, or exactly one Microsoft.EventGrid.SubscriptionValidationEvent.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Array, MinItems = 1, MaxItems = 100,
                            Items = new OpenApiSchema
                            {
                                OneOf =
                                [
                                    domainEventSchema,
                                    new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.Object,
                                        Required = new HashSet<string> { "id", "eventType", "data" },
                                        Properties = new Dictionary<string, IOpenApiSchema>
                                        {
                                            ["id"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                            ["eventType"] = new OpenApiSchema
                                            {
                                                Type = JsonSchemaType.String,
                                                Enum = [System.Text.Json.Nodes.JsonValue.Create("Microsoft.EventGrid.SubscriptionValidationEvent")]
                                            },
                                            ["data"] = new OpenApiSchema
                                            {
                                                Type = JsonSchemaType.Object,
                                                Required = new HashSet<string> { "validationCode" },
                                                Properties = new Dictionary<string, IOpenApiSchema>
                                                {
                                                    ["validationCode"] = new OpenApiSchema { Type = JsonSchemaType.String }
                                                }
                                            }
                                        }
                                    }
                                ]
                            }
                        }
                    }
                }
            };
        }
    });
    options.AddDocumentTransformer<FrontendOpenApiTransformer>();
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
}).RequireAuthorization().RequireRateLimiting("ingress").WithSummary("Validate and durably accept a provider or distributor callback")
    .Describe("AcceptCallback", "Callbacks", "Supply a version-1 DomainEvent with a stable event ID, tenant/book/distributor IDs, sequence and eventTime. PrintProvider may report BooksPrinted/BooksDelivered only for an existing matching print order, with exactly the ordered quantity and in the Requested-or-Submitted-to-Printed-to-Delivered order. Matching retries return 202; additional or out-of-order fulfillment callbacks are rejected. Distributor may report BooksDistributed, BooksSold, StockUpdated, LowStockAlert, StockDepleted or BookAvailableForSale only for itself. StockUpdated is absolute; deliveries credit stock and sales/distribution debit it. Quantities are 0 through 1,000,000; low/depleted alerts match the configured threshold/zero. Sale listings must use an allowlisted HTTPS host. Conflicting event IDs return 409. Accepted events are stored in SQL before publication. Limited to 100 requests per identity per minute.", 400, 401, 403, 409, 413, 429, 500);
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
        return TypedResults.Ok(new SubscriptionValidationResponse(code.GetString()!));
    }
    await service.ForwardNotificationsAsync(events, context.Request.Headers.Authorization.ToString(), ct);
    return TypedResults.Accepted((string?)null);
}).RequireAuthorization("EventGrid").RequireRateLimiting("ingress").WithSummary("Validate Event Grid subscriptions and forward notifications privately")
    .Produces<SubscriptionValidationResponse>(200).Produces(202)
    .Describe("AcceptEventGridNotifications", "Notifications", "Requires the EventGrid application role, including validation handshakes. POST exactly one Microsoft.EventGrid.SubscriptionValidationEvent to receive its data.validationCode as validationResponse (200). Otherwise POST 1 through 100 DomainEvents of types EditorAssigned, ProofprintCreated, ManuscriptEdited, ManuscriptRevised, ManuscriptApproved, BookAvailableForSale, LowStockAlert or StockDepleted. Returns 202 only after private Notifications ingress durably accepts the batch. Does not wait for email/provider delivery. Limited to 100 requests per identity per minute.", 400, 401, 403, 413, 429, 500, 503);
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
    }).RequireAuthorization().RequireRateLimiting("ingress").ExcludeFromDescription();
app.MapGet("/", async (HttpContext context, IFrontendService service, CancellationToken ct) =>
{
    using var response = await service.ProxyAsync(context.Request, "", ct);
    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
    await response.Content.CopyToAsync(context.Response.Body, ct);
}).AllowAnonymous().WithSummary("Open the BookFlow web client").Produces<string>(200, contentType: "text/html")
    .Describe("GetWebClient", "Web client", "Returns the browser client from the private WebApp without authentication. Application operations and downloads require a bearer token. The client keeps the token only in page memory.", 500, 503);
await app.RunAsync();
