using System.Security.Cryptography;
using System.Text;
using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Infrastructure.Middleware;
using BookFlow.LocalProviders;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Development providers must never run outside Development.");
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddBookFlowOpenApi(builder.Environment.ApplicationName, provider: true);
builder.Services.AddSingleton<IDevelopmentProvider>(
    new DevelopmentProvider(Hosting.Required(builder.Configuration, "Providers:StoreRoot")));
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOpenApi();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next(context);
        return;
    }
    var expected = Encoding.UTF8.GetBytes("Bearer " + Hosting.Required(app.Configuration, "Providers:Token"));
    var actual = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
    if (!CryptographicOperations.FixedTimeEquals(expected, actual))
    {
        context.Response.StatusCode = 401;
        return;
    }
    await next(context);
});
app.MapGet("/health/live", (CancellationToken _) => TypedResults.Ok())
    .WithSummary("Check development provider liveness")
    .Describe("GetLiveness", "Health", "Unauthenticated process probe. Returns an empty 200 while the development provider is responsive.");
app.MapGet("/tokens/{role}", (string role, CancellationToken _) =>
{
    if (role is not ("Admin" or "Author" or "Editor" or "Publisher" or "Distributor" or "PrintProvider" or "EventGrid"))
        throw new ArgumentException("Unsupported development role.");
    return TypedResults.Ok(new DevelopmentTokenResponse(
        Hosting.LocalToken(app.Configuration, role.ToLowerInvariant(),
            Guid.Parse(Hosting.Required(app.Configuration, "BookFlow:DemoTenant")), role)));
}).ProviderBearer().WithSummary("Issue a short-lived local JWT for a known demo identity")
    .Describe("CreateDevelopmentToken", "Development authentication", "Requires the provider bearer token, not an application JWT. Supported roles: Admin, Author, Editor, Publisher, Distributor, PrintProvider and EventGrid (case-sensitive). The JWT identity is the lowercase role in the configured demo tenant and expires after one hour. This development-only endpoint cannot be enabled in Production.", 400, 401, 500);
app.MapPost("/proofprints", async (HttpContext context, IDevelopmentProvider provider, CancellationToken ct) =>
{
    using var body = new MemoryStream();
    await context.Request.Body.CopyToAsync(body, ct);
    await provider.RecordAsync(context.Request.Headers["Idempotency-Key"].ToString(), body.ToArray(), "proofprint", ct);
    return TypedResults.File(DevelopmentProvider.Pdf(), "application/pdf");
}).ProviderBearer().WithSummary("Return a synthetic PDF, not a rendering of the supplied manuscript")
    .Accepts<byte[]>("application/octet-stream").BinaryResponse("application/pdf")
    .Describe("GenerateDevelopmentProofprint", "Development providers", "Requires the provider bearer token and Idempotency-Key header (1 through 300 characters). POST raw manuscript bytes. Records the request and returns a synthetic PDF that does not render the manuscript. Matching retries preserve the provider reference; reusing a key for different bytes returns 409.", 400, 401, 409, 413, 500);
app.MapPost("/orders", async (PrintSubmission submission, HttpContext context, IDevelopmentProvider provider, CancellationToken ct) =>
{
    var reference = await provider.RecordAsync(context.Request.Headers["Idempotency-Key"].ToString(),
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(submission, EventJson.Options), "order", ct);
    return TypedResults.Ok(new PrintProviderResponse(reference));
}).ProviderBearer().WithSummary("Record an idempotent synthetic print order")
    .Describe("CreateDevelopmentPrintOrder", "Development providers", "Requires the provider bearer token and Idempotency-Key header (1 through 300 characters). Supply bookId, title, isbn, distributorId and quantity. Persists a synthetic stable provider order reference; no real printing or delivery is performed. Matching retries return the same reference; changed requests under the same key return 409.", 400, 401, 409, 500);
foreach (var route in new[] { "/emails", "/publisher-notifications" })
{
    app.MapPost(route, async (NotificationMessage message, HttpContext context, IDevelopmentProvider provider, CancellationToken ct) =>
    {
        await provider.RecordAsync(context.Request.Headers["Idempotency-Key"].ToString(),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, EventJson.Options), context.Request.Path, ct);
        return TypedResults.Accepted((string?)null);
    }).ProviderBearer().WithSummary(route == "/emails" ? "Record a synthetic email" : "Record a synthetic publisher notification")
        .Describe(route == "/emails" ? "SendDevelopmentEmail" : "SendDevelopmentPublisherNotification",
            "Development providers",
            route == "/emails"
                ? "Requires the provider bearer token and Idempotency-Key header (1 through 300 characters). Supply destination email, template, protected link and publisherApi=false. Records the synthetic delivery; no email is sent. Matching retries are idempotent; different payloads under the same key return 409."
                : "Requires the provider bearer token and Idempotency-Key header (1 through 300 characters). Supply destination publisher identity, template, link and publisherApi=true. Records synthetic book-orderability notification; no external publisher is contacted. Matching retries are idempotent; different payloads under the same key return 409.",
            400, 401, 409, 500);
}
await app.RunAsync();
