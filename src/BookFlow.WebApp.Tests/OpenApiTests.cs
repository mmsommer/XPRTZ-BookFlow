using BookFlow.Infrastructure;
using BookFlow.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Moq;

namespace BookFlow.WebApp.Tests;

public sealed class OpenApiTests
{
    [Fact]
    public async Task Every_application_operation_has_contract_security_and_unique_identity()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Endpoints).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddBookFlowOpenApi("BookFlow.WebApp");
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped(_ => new Mock<IManuscriptService>(MockBehavior.Strict).Object);
        await using var app = builder.Build();
        app.MapManuscripts();
        await app.StartAsync();

        var document = await app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
            .GetOpenApiDocumentAsync(CancellationToken.None);
        var operations = document.Paths.Values.SelectMany(x => x.Operations!.Values).ToArray();
        operations.Should().HaveCount(17);
        operations.Select(x => x.OperationId).Should().OnlyHaveUniqueItems();
        document.Components!.SecuritySchemes.Should().ContainKey("Bearer");
        foreach (var operation in operations)
        {
            operation.OperationId.Should().NotBeNullOrWhiteSpace();
            operation.Summary.Should().NotBeNullOrWhiteSpace();
            operation.Description.Should().NotBeNullOrWhiteSpace();
            operation.Tags.Should().NotBeEmpty();
            operation.Security.Should().ContainSingle();
            operation.Responses.Should().ContainKey("401").And.ContainKey("403").And.ContainKey("500");
        }
        var upload = document.Paths["/api/manuscripts"].Operations![HttpMethod.Post];
        upload.RequestBody!.Content.Should().ContainKey("application/json");
        upload.Responses.Should().ContainKey("201").And.ContainKey("409").And.ContainKey("413");
        upload.Responses!["201"].Headers.Should().ContainKey("Location");
        document.Paths["/api/manuscripts/{id}/proofprints/{proofprintId}/file"].Operations![HttpMethod.Get]
            .Responses!["200"].Content.Should().ContainKey("application/pdf");
        document.Components.Schemas.Should().ContainKey(nameof(ManuscriptStatus));
        var json = await document.SerializeAsync(OpenApiSpecVersion.OpenApi3_1, "json");
        var parsed = OpenApiDocument.Parse(json, "json");
        parsed.Diagnostic!.Errors.Should().BeEmpty();
        await app.StopAsync();
    }
}
