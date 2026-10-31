using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BookFlow.ApiFrontend;

public sealed class FrontendOpenApiTransformer(IHttpClientFactory clients) : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        using var response = await clients.CreateClient("webapp").GetAsync("openapi/v1.json", ct);
        response.EnsureSuccessStatusCode();
        var parsed = OpenApiDocument.Parse(await response.Content.ReadAsStringAsync(ct), "json");
        if (parsed.Diagnostic?.Errors.Count > 0 || parsed.Document is null)
            throw new InvalidOperationException("The private WebApp returned an invalid OpenAPI document.");
        var upstream = parsed.Document;
        foreach (var (path, item) in upstream.Paths.Where(x => x.Key.StartsWith("/api/", StringComparison.Ordinal)))
        {
            foreach (var operation in item.Operations?.Values.AsEnumerable() ?? [])
            {
                operation.Description += " Exposed through the API frontend with a limit of 100 requests per identity per one-minute window.";
                operation.Responses ??= new OpenApiResponses();
                operation.Responses["429"] = new OpenApiResponse
                {
                    Description = "Ingress rate limit exceeded. Retry after the current one-minute window.",
                    Content = operation.Responses.TryGetValue("401", out var unauthorized) ? unauthorized.Content : null
                };
                operation.Responses["503"] = new OpenApiResponse
                {
                    Description = "The private application service is unavailable.",
                    Content = operation.Responses.TryGetValue("500", out var failed) ? failed.Content : null
                };
                operation.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = []
                }];
            }
            document.Paths.Add(path, item);
        }
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        foreach (var (name, schema) in upstream.Components?.Schemas ?? new Dictionary<string, IOpenApiSchema>())
        {
            if (!document.Components.Schemas.ContainsKey(name))
                document.Components.Schemas.Add(name, schema);
        }
        document.Info.Description += " This document includes the concrete application API routes forwarded to the private WebApp; wildcard proxy implementation routes are omitted.";
    }
}
