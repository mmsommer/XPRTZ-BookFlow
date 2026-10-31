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
builder.Services.AddOpenApi();
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
app.MapGet("/health/live", () => TypedResults.Ok());
app.MapGet("/tokens/{role}", (string role) =>
{
    if (role is not ("Admin" or "Author" or "Editor" or "Publisher" or "Distributor" or "PrintProvider" or "EventGrid"))
        throw new ArgumentException("Unsupported development role.");
    return TypedResults.Ok(new
    {
        token = Hosting.LocalToken(app.Configuration, role.ToLowerInvariant(),
            Guid.Parse(Hosting.Required(app.Configuration, "BookFlow:DemoTenant")), role)
    });
}).WithSummary("Issue a short-lived local JWT for a known demo identity");
app.MapPost("/proofprints", async (HttpContext context, IDevelopmentProvider provider, CancellationToken ct) =>
{
    using var body = new MemoryStream();
    await context.Request.Body.CopyToAsync(body, ct);
    await provider.RecordAsync(context.Request.Headers["Idempotency-Key"].ToString(), body.ToArray(), "proofprint", ct);
    return TypedResults.File(DevelopmentProvider.Pdf(), "application/pdf");
}).WithSummary("Return a synthetic PDF, not a rendering of the supplied manuscript");
app.MapPost("/orders", async (PrintSubmission submission, HttpContext context, IDevelopmentProvider provider, CancellationToken ct) =>
{
    var reference = await provider.RecordAsync(context.Request.Headers["Idempotency-Key"].ToString(),
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(submission, EventJson.Options), "order", ct);
    return TypedResults.Ok(new PrintProviderResponse(reference));
}).WithSummary("Record an idempotent synthetic print order");
foreach (var route in new[] { "/emails", "/publisher-notifications" })
{
    app.MapPost(route, async (NotificationMessage message, HttpContext context, IDevelopmentProvider provider, CancellationToken ct) =>
    {
        await provider.RecordAsync(context.Request.Headers["Idempotency-Key"].ToString(),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, EventJson.Options), context.Request.Path, ct);
        return TypedResults.Accepted((string?)null);
    }).WithSummary("Record a synthetic notification without contacting external services");
}
await app.RunAsync();
