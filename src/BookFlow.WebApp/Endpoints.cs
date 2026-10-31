using System.Security.Claims;
using BookFlow.Infrastructure;

namespace BookFlow.WebApp;

/// <summary>Maps tenant-scoped manuscript, book, participant, and inventory HTTP operations.</summary>
public static class Endpoints
{
    /// <summary>Maps participant, manuscript, book, print-order, stock, and download endpoints.</summary>
    /// <param name="app">The web application to configure.</param>
    public static void MapManuscripts(this WebApplication app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();
        group.MapPost("/participants", async (ClaimsPrincipal user, CreateParticipantRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            await service.RegisterAsync(Hosting.GetActor(user), request, ct);
            return TypedResults.Created($"/api/participants/{request.Id}", new CreateParticipantResponse(request.Id));
        }).WithSummary("Register a participant (administrator only)")
            .Describe("CreateParticipant", "Participants", "Admin only. Registers an active Author, Editor, Publisher or Distributor in the JWT tenant. Only one Publisher may be registered per tenant. Returns the participant ID and a Location header.", 400, 401, 403, 409, 500);
        group.MapGet("/participants/{id}", async (string id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ParticipantAsync(Hosting.GetActor(user), id, ct)))
            .WithSummary("Read a registered participant (administrator only)")
            .Describe("GetParticipant", "Participants", "Admin only. Returns registration, contact details, role and active status for the specified participant within the caller's tenant.", 401, 403, 404, 500);
        group.MapGet("/manuscripts", async (ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(Hosting.GetActor(user), ct))).WithSummary("List authorized manuscripts")
            .Describe("ListManuscripts", "Manuscripts", "Returns at most 100 manuscripts ordered by ID. Authors see their own submissions, editors their assignments, publishers their titles, and administrators their tenant. Other roles receive an empty list.", 401, 403, 500);
        group.MapGet("/manuscripts/{id:guid}", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read manuscript state")
            .Describe("GetManuscript", "Manuscripts", "Returns authoritative manuscript status and the asynchronously projected progress status/sequence. Requires the owning Author, assigned Editor, responsible Publisher or tenant Admin.", 400, 401, 403, 404, 500);
        group.MapPost("/manuscripts", async (ClaimsPrincipal user, CreateManuscriptRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(Hosting.GetActor(user), request, ct);
            return TypedResults.Created($"/api/manuscripts/{result.Id}", result);
        }).WithSummary("Upload a manuscript and initial version")
            .Describe("CreateManuscript", "Manuscripts", "Registered Author only. Supply a title, registered tenant Publisher, idempotencyKey and a base64 file decoding to 1 byte through 10 MB. Stores an immutable initial version and durable ManuscriptUploaded event. An identical retry returns the same manuscript; a changed payload with the same key returns 409.", 400, 401, 403, 409, 413, 500);
        group.MapGet("/manuscripts/{id:guid}/versions", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.VersionsAsync(Hosting.GetActor(user), id, ct))).WithSummary("List immutable versions, highlights and feedback")
            .Describe("ListManuscriptVersions", "Versions", "Returns all immutable versions in version-number order with parent IDs, editor highlights, author feedback and optional generated proofprint IDs. Requires manuscript access.", 400, 401, 403, 404, 500);
        group.MapGet("/manuscripts/{id:guid}/history", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.HistoryAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read projected lifecycle history")
            .Describe("GetManuscriptHistory", "Manuscripts", "Returns persisted lifecycle event IDs, sequences, statuses and timestamps ordered by sequence. This asynchronous projection may lag the authoritative manuscript state.", 400, 401, 403, 404, 500);
        group.MapPost("/manuscripts/{id:guid}/edits", async (Guid id, ClaimsPrincipal user, CreateVersionRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.VersionAsync(Hosting.GetActor(user), id, request, true, ct);
            return TypedResults.Created($"/api/manuscripts/{id}/versions/{result.Id}/file", result);
        }).WithSummary("Persist an editor version and highlights")
            .Describe("CreateEditorVersion", "Versions", "Assigned Editor only, while AssignedForEditing or AwaitingEditorReview. parentVersionId must identify the latest version. Stores a new immutable base64 file and optional highlights with valid start/end offsets and text, transitions to AwaitingAuthorResponse, and queues ManuscriptEdited. Idempotent by request key.", 400, 401, 403, 404, 409, 413, 500);
        group.MapPost("/manuscripts/{id:guid}/revisions", async (Guid id, ClaimsPrincipal user, CreateVersionRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.VersionAsync(Hosting.GetActor(user), id, request, false, ct);
            return TypedResults.Created($"/api/manuscripts/{id}/versions/{result.Id}/file", result);
        }).WithSummary("Persist an author revision and feedback")
            .Describe("CreateAuthorRevision", "Versions", "Owning Author only, while AwaitingAuthorResponse. Supply the latest parentVersionId, new base64 file, nonempty feedback and no editor highlights. Transitions to AwaitingEditorReview and queues ManuscriptRevised. Idempotent by request key.", 400, 401, 403, 404, 409, 413, 500);
        group.MapPost("/manuscripts/{id:guid}/approval", async (Guid id, ClaimsPrincipal user, ApproveManuscriptRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.ApproveAsync(Hosting.GetActor(user), id, request, ct);
            return TypedResults.Created($"/api/books/{result.Id}", result);
        }).WithSummary("Approve and request initial printing")
            .Describe("ApproveManuscript", "Publication", "Assigned Editor only, while AssignedForEditing or AwaitingEditorReview. Supply an unused checksum-valid ISBN-13, an active tenant Distributor and quantity 1 through 1,000,000. Creates an orderable Book, the initial PrintOrder and durable ManuscriptApproved event. Approval does not imply physical stock. Idempotent by request key.", 400, 401, 403, 404, 409, 500);
        group.MapGet("/books", async (ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.BooksAsync(Hosting.GetActor(user), ct))).WithSummary("List authorized published titles")
            .Describe("ListBooks", "Books", "Lists tenant books visible through authorized manuscripts. Distributors can list tenant titles. Orderable indicates publishing approval, not distributor inventory.", 401, 403, 500);
        group.MapGet("/books/{id:guid}", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.BookAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read an authorized book")
            .Describe("GetBook", "Books", "Returns a title, ISBN, manuscript ID and orderability for a book visible in ListBooks. Returns 404 when the book is not visible.", 400, 401, 403, 404, 500);
        group.MapGet("/books/{id:guid}/stock", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.StockAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read stock by title and distributor")
            .Describe("GetBookStock", "Inventory", "Returns available stock, printed/delivered/distributed/sold totals, alert, sequence and timestamp per distributor. A registered Distributor sees only its own stock; other authorized manuscript participants see all distributor records for the book. An empty array means no stock has been recorded.", 400, 401, 403, 404, 500);
        group.MapPost("/orders", async (ClaimsPrincipal user, CreatePrintOrderRequest request, IManuscriptService service, CancellationToken ct) =>
        {
            var result = await service.OrderAsync(Hosting.GetActor(user), request, ct);
            return TypedResults.Created($"/api/orders/{result.Id}", result);
        }).WithSummary("Request an additional print run")
            .Describe("CreatePrintOrder", "Orders", "Responsible Publisher only. The book must be orderable, distributor active in the tenant and quantity 1 through 1,000,000. Persists the order and PrintOrderRequested event before returning. Printing is asynchronous and does not credit inventory until delivery confirmation. Idempotent by request key.", 400, 401, 403, 404, 409, 500);
        group.MapGet("/orders/{id:guid}", async (Guid id, ClaimsPrincipal user, IManuscriptService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetOrderAsync(Hosting.GetActor(user), id, ct))).WithSummary("Read print order status")
            .Describe("GetPrintOrder", "Orders", "Returns quantity, distributor, Requested/Submitted/Printed/Delivered status and optional provider reference. Requires book access; a Distributor may read only orders addressed to it.", 400, 401, 403, 404, 500);
        group.MapGet("/manuscripts/{id:guid}/versions/{versionId:guid}/file",
            async (Guid id, Guid versionId, ClaimsPrincipal user, IManuscriptService service, HttpContext context, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.File(await service.FileAsync(Hosting.GetActor(user), id, versionId, false, ct),
                    "application/octet-stream", "manuscript.bin");
            }).WithSummary("Download an authorized manuscript version").BinaryResponse("application/octet-stream")
                .Describe("DownloadManuscriptVersion", "Files", "Downloads the specified immutable version as manuscript.bin. The version must belong to this manuscript and the caller must have manuscript access. Uses bearer authorization for every download and Cache-Control: no-store; no public Blob URL is exposed.", 400, 401, 403, 404, 500);
        group.MapGet("/manuscripts/{id:guid}/proofprints/{proofprintId:guid}/file",
            async (Guid id, Guid proofprintId, ClaimsPrincipal user, IManuscriptService service, HttpContext context, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.File(await service.FileAsync(Hosting.GetActor(user), id, proofprintId, true, ct),
                    "application/pdf", "proofprint.pdf");
            }).WithSummary("Download an authorized proofprint").BinaryResponse("application/pdf")
                .Describe("DownloadProofprint", "Files", "Downloads a generated proofprint as proofprint.pdf. The proofprint's source version must belong to this manuscript and the caller must have manuscript access. Bearer authorization is required and caching is disabled.", 400, 401, 403, 404, 500);
    }
}
