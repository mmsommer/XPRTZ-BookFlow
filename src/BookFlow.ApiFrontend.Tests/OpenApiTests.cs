using System.Net;
using BookFlow.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Moq;
using Moq.Protected;

namespace BookFlow.ApiFrontend.Tests;

/// <summary>Tests frontend OpenAPI composition and upstream failure handling.</summary>
public sealed class OpenApiTests
{
    /// <summary>Verifies frontend composition includes concrete paths, components, and frontend errors.</summary>
    [Fact]
    public async Task Composes_concrete_application_paths_and_components_with_frontend_errors()
    {
        const string upstream = """
            {
              "openapi": "3.1.0",
              "info": { "title": "WebApp", "version": "v1" },
              "paths": {
                "/api/books": {
                  "get": {
                    "operationId": "ListBooks", "summary": "Books", "description": "Authorized books.",
                    "responses": {
                      "200": { "description": "OK", "content": { "application/json": {
                        "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Book" } }
                      } } },
                      "401": { "description": "Unauthorized" },
                      "500": { "description": "Failed" }
                    }
                  }
                },
                "/health/live": { "get": { "responses": { "200": { "description": "OK" } } } }
              },
              "components": { "schemas": { "Book": { "type": "object", "properties": { "id": { "type": "string" } } } } }
            }
            """;
        var factory = Factory(HttpStatusCode.OK, upstream);
        var builder = Builder(factory.Object);
        await using var app = builder.Build();
        app.MapGet("/callbacks", () => TypedResults.Accepted((string?)null))
            .WithSummary("Callback").Describe("AcceptCallback", "Callbacks", "Accept callback.");
        await app.StartAsync();
        var document = await app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
            .GetOpenApiDocumentAsync(CancellationToken.None);

        document.Paths.Keys.Should().BeEquivalentTo("/callbacks", "/api/books");
        document.Components!.Schemas.Should().ContainKey("Book");
        var operation = document.Paths["/api/books"].Operations![HttpMethod.Get];
        operation.OperationId.Should().Be("ListBooks");
        operation.Description.Should().Contain("100 requests");
        operation.Responses.Should().ContainKey("429").And.ContainKey("503");
        operation.Security.Should().ContainSingle();
        var serialized = await document.SerializeAsync(OpenApiSpecVersion.OpenApi3_1, "json");
        OpenApiDocument.Parse(serialized, "json").Diagnostic!.Errors.Should().BeEmpty();
        factory.Verify(x => x.CreateClient("webapp"), Times.Once);
        await app.StopAsync();
    }

    /// <summary>Verifies upstream failure does not return incomplete successful document.</summary>
    [Fact]
    public async Task Upstream_failure_does_not_return_incomplete_successful_document()
    {
        var builder = Builder(Factory(HttpStatusCode.ServiceUnavailable, "").Object);
        await using var app = builder.Build();
        app.MapGet("/", () => TypedResults.Ok()).WithSummary("Client").Describe("GetClient", "Client", "Client.");
        await app.StartAsync();

        await FluentActions.Awaiting(() => app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
            .GetOpenApiDocumentAsync(CancellationToken.None)).Should().ThrowAsync<HttpRequestException>();
        await app.StopAsync();
    }

    private static WebApplicationBuilder Builder(IHttpClientFactory factory)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddBookFlowOpenApi("BookFlow.ApiFrontend");
        builder.Services.AddSingleton(factory);
        builder.Services.Configure<OpenApiOptions>("v1", options =>
            options.AddDocumentTransformer<FrontendOpenApiTransformer>());
        return builder;
    }

    private static Mock<IHttpClientFactory> Factory(HttpStatusCode status, string body)
    {
        var http = new Mock<HttpMessageHandler>();
        http.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(status) { Content = new StringContent(body) });
        var client = new HttpClient(http.Object) { BaseAddress = new Uri("http://webapp.test/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("webapp")).Returns(client);
        return factory;
    }
}
