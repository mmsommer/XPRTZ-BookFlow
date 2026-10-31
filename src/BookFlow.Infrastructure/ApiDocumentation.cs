using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace BookFlow.Infrastructure;

public static class ApiDocumentation
{
    public static void AddBookFlowOpenApi(this IServiceCollection services, string applicationName, bool provider = false)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = applicationName,
                    Version = "v1",
                    Description = provider
                        ? "Development-only provider test doubles. Protected operations use the configured provider bearer token, not an application JWT. No real books or emails are produced."
                        : "BookFlow publishing workflows. Protected operations require a bearer JWT with oid, tid and roles claims. Access is scoped to the participant and publisher tenant. Errors use Problem Details."
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = provider ? null : "JWT",
                    Description = provider
                        ? "Providers:Token from the ignored local configuration."
                        : "Microsoft Entra JWT, or a short-lived development JWT in Local mode."
                };
                foreach (var path in document.Paths.Values)
                foreach (var operation in path.Operations?.Values.AsEnumerable() ?? [])
                {
                    if (operation.Security is { Count: > 0 })
                        operation.Security = [new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference("Bearer", document)] = []
                        }];
                }
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var protectedEndpoint = !metadata.OfType<IAllowAnonymous>().Any() &&
                    (metadata.OfType<IAuthorizeData>().Any() || metadata.OfType<ProviderBearerMetadata>().Any());
                operation.Security = protectedEndpoint
                    ? [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer")] = [] }]
                    : [];
                if (metadata.OfType<BinaryResponseMetadata>().SingleOrDefault() is { } binary)
                {
                    operation.Responses ??= new OpenApiResponses();
                    operation.Responses["200"] = new OpenApiResponse
                    {
                        Description = "File content returned with Content-Disposition attachment.",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            [binary.ContentType] = new()
                            {
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                            }
                        }
                    };
                }
                if (provider && context.Description.HttpMethod == "POST")
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "Idempotency-Key", In = ParameterLocation.Header, Required = true,
                        Description = "Stable key for this provider operation. Matching retries preserve the original result; changed payloads under the same key return 409.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 300 }
                    });
                    if (context.Description.RelativePath == "proofprints")
                        operation.RequestBody = new OpenApiRequestBody
                        {
                            Required = true,
                            Description = "Raw manuscript bytes; this development provider does not render them.",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/octet-stream"] = new()
                                {
                                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                                }
                            }
                        };
                }
                foreach (var response in operation.Responses ?? [])
                {
                    response.Value.Description = response.Key switch
                    {
                        "200" => "Request succeeded.",
                        "201" => "Resource created or idempotently returned. Location identifies the resource.",
                        "202" => "Durably accepted. Background processing or provider delivery may still be pending.",
                        "400" => "Malformed payload, invalid values, unsupported event or validation failure.",
                        "401" => "Bearer credentials are missing, invalid or expired.",
                        "403" => "Caller lacks the required role, tenant membership or resource relationship.",
                        "404" => "The resource does not exist or is not visible in this tenant.",
                        "409" => "Invalid state transition, concurrent modification or conflicting idempotency key.",
                        "413" => "Request body exceeds the server limit.",
                        "429" => "Ingress rate limit exceeded. Retry after the current one-minute window.",
                        "500" => "Unexpected server failure.",
                        "503" => "A required dependency is unavailable.",
                        _ => response.Value.Description
                    };
                    if (response.Key == "201" && response.Value is OpenApiResponse created)
                    {
                        created.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        created.Headers["Location"] = new OpenApiHeader
                        {
                            Description = "Relative URL identifying the created or idempotently returned resource.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri-reference" }
                        };
                    }
                }
                return Task.CompletedTask;
            });
        });
    }

    public static RouteHandlerBuilder Describe(this RouteHandlerBuilder endpoint,
        string operationId, string tag, string description, params int[] errors)
    {
        endpoint.WithName(operationId).WithTags(tag).WithDescription(description);
        foreach (var status in errors.Distinct())
            endpoint.ProducesProblem(status);
        return endpoint;
    }

    public static RouteHandlerBuilder ProviderBearer(this RouteHandlerBuilder endpoint) =>
        endpoint.WithMetadata(new ProviderBearerMetadata());

    public static RouteHandlerBuilder BinaryResponse(this RouteHandlerBuilder endpoint, string contentType) =>
        endpoint.WithMetadata(new BinaryResponseMetadata(contentType));

    private sealed record ProviderBearerMetadata;
    private sealed record BinaryResponseMetadata(string ContentType);
}
