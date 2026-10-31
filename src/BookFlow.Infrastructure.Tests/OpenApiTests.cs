using BookFlow.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace BookFlow.Infrastructure.Tests;

/// <summary>Tests OpenAPI security and development provider body contracts.</summary>
public sealed class OpenApiTests
{
    /// <summary>Verifies anonymous and provider operations have correct security and raw body contract.</summary>
    [Fact]
    public async Task Anonymous_and_provider_operations_have_correct_security_and_raw_body_contract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddBookFlowOpenApi("Development providers", provider: true);
        await using var app = builder.Build();
        app.MapGet("/health/live", () => Microsoft.AspNetCore.Http.TypedResults.Ok())
            .WithSummary("Liveness").Describe("GetLiveness", "Health", "Anonymous liveness check.");
        app.MapPost("/proofprints", () => Microsoft.AspNetCore.Http.TypedResults.File("%PDF-"u8.ToArray(), "application/pdf"))
            .ProviderBearer().WithSummary("Synthetic PDF")
            .Describe("GenerateProofprint", "Providers", "Provider-authenticated synthetic PDF.", 400, 401, 409);
        await app.StartAsync();
        var document = await app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
            .GetOpenApiDocumentAsync(CancellationToken.None);
        document.Paths["/health/live"].Operations![HttpMethod.Get].Security.Should().BeEmpty();
        var proofprint = document.Paths["/proofprints"].Operations![HttpMethod.Post];
        proofprint.Security.Should().ContainSingle();
        var header = proofprint.Parameters!.Single(x => x.Name == "Idempotency-Key");
        header.Required.Should().BeTrue();
        header.In.Should().Be(ParameterLocation.Header);
        proofprint.RequestBody!.Content!["application/octet-stream"].Schema!.Format.Should().Be("binary");
        document.Components!.SecuritySchemes!["Bearer"].BearerFormat.Should().BeNull();
        await app.StopAsync();
    }
}
