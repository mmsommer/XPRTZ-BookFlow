using System.Security.Claims;
using BookFlow.Infrastructure;

namespace BookFlow.WebApp;

public static class Endpoints
{
    public static void MapManuscripts(this WebApplication app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();
        group.MapPost("/participants", async (ClaimsPrincipal user, CreateParticipantRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            await service.RegisterAsync(Hosting.GetActor(user), request, ct);
            return TypedResults.Created($"/api/participants/{request.Id}", new { request.Id });
        }).WithSummary("Register a participant (administrator only)");
        group.MapGet("/participants/{id}", async (string id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ParticipantAsync(Hosting.GetActor(user), id, ct)))
            .WithSummary("Read a registered participant (administrator only)");
        group.MapGet("/manuscripts", async (ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(Hosting.GetActor(user), ct))).WithSummary("List authorized manuscripts");
        group.MapGet("/manuscripts/{id:guid}", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read manuscript state");
        group.MapPost("/manuscripts", async (ClaimsPrincipal user, CreateManuscriptRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(Hosting.GetActor(user), request, ct);
            return TypedResults.Created($"/api/manuscripts/{result.Id}", result);
        }).WithSummary("Upload a manuscript and initial version");
        group.MapGet("/manuscripts/{id:guid}/versions", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.VersionsAsync(Hosting.GetActor(user), id, ct))).WithSummary("List immutable versions, highlights and feedback");
        group.MapGet("/manuscripts/{id:guid}/history", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.HistoryAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read projected lifecycle history");
        group.MapPost("/manuscripts/{id:guid}/edits", async (Guid id, ClaimsPrincipal user, CreateVersionRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.VersionAsync(Hosting.GetActor(user), id, request, true, ct);
            return TypedResults.Created($"/api/manuscripts/{id}/versions/{result.Id}/file", result);
        }).WithSummary("Persist an editor version and highlights");
        group.MapPost("/manuscripts/{id:guid}/revisions", async (Guid id, ClaimsPrincipal user, CreateVersionRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.VersionAsync(Hosting.GetActor(user), id, request, false, ct);
            return TypedResults.Created($"/api/manuscripts/{id}/versions/{result.Id}/file", result);
        }).WithSummary("Persist an author revision and feedback");
        group.MapPost("/manuscripts/{id:guid}/approval", async (Guid id, ClaimsPrincipal user, ApproveManuscriptRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.ApproveAsync(Hosting.GetActor(user), id, request, ct);
            return TypedResults.Created($"/api/books/{result.Id}", result);
        }).WithSummary("Approve and request initial printing");
        group.MapGet("/books", async (ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.BooksAsync(Hosting.GetActor(user), ct))).WithSummary("List authorized published titles");
        group.MapGet("/books/{id:guid}", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
        {
            var books = await service.BooksAsync(Hosting.GetActor(user), ct);
            return TypedResults.Ok(books.SingleOrDefault(x => x.Id == id) ?? throw new BookFlow.Core.NotFoundException());
        }).WithSummary("Read an authorized book");
        group.MapGet("/books/{id:guid}/stock", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.StockAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read stock by title and distributor");
        group.MapPost("/orders", async (ClaimsPrincipal user, CreatePrintOrderRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.OrderAsync(Hosting.GetActor(user), request, ct);
            return TypedResults.Created($"/api/orders/{result.Id}", result);
        }).WithSummary("Request an additional print run");
        group.MapGet("/orders/{id:guid}", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetOrderAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read print order status");
        group.MapGet("/manuscripts/{id:guid}/versions/{versionId:guid}/file",
            async (Guid id, Guid versionId, ClaimsPrincipal user, IManuscriptService service, HttpContext context, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.File(await service.FileAsync(Hosting.GetActor(user), id, versionId, false, ct),
                    "application/octet-stream", "manuscript.bin");
            }).WithSummary("Download an authorized manuscript version");
        group.MapGet("/manuscripts/{id:guid}/proofprints/{proofprintId:guid}/file",
            async (Guid id, Guid proofprintId, ClaimsPrincipal user, IManuscriptService service, HttpContext context, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.File(await service.FileAsync(Hosting.GetActor(user), id, proofprintId, true, ct),
                    "application/pdf", "proofprint.pdf");
            }).WithSummary("Download an authorized proofprint");
    }
}
