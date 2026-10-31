using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using BookFlow.Core;
using BookFlow.Infrastructure.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Data.SqlClient;

namespace BookFlow.Infrastructure;

public static class Hosting
{
    public static void AddBookFlow(this WebApplicationBuilder builder, bool outbox = false, string? consumer = null)
    {
        var config = builder.Configuration;
        var local = config["BookFlow:Mode"] == "Local";
        if (local && !builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Local mode is restricted to Development.");
        var credential = new DefaultAzureCredential();
        builder.Services.AddDbContext<BookFlowDbContext>(options =>
        {
            var connection = Required(config, "ConnectionStrings:BookFlow");
            if (local)
                options.UseSqlite(connection);
            else
            {
                var sql = new SqlConnectionStringBuilder(connection);
                if (sql.Authentication is not (SqlAuthenticationMethod.ActiveDirectoryDefault or
                    SqlAuthenticationMethod.ActiveDirectoryManagedIdentity or SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity) ||
                    sql.TrustServerCertificate || sql.Encrypt == SqlConnectionEncryptOption.Optional ||
                    !string.IsNullOrEmpty(sql.Password))
                    throw new InvalidOperationException("Azure SQL requires Entra authentication and validated TLS.");
                options.UseSqlServer(connection);
            }
        });
        builder.Services.AddScoped<IEventTransaction, EventTransaction>();
        builder.Services.AddOptions<InventoryOptions>().BindConfiguration("Inventory")
            .Validate(options => options.LowStockThreshold is >= 1 and <= 1000000, "Invalid low-stock threshold.")
            .ValidateOnStart();
        if (local)
            builder.Services.AddSingleton<IBlobStore>(new FileBlobStore(Required(config, "BookFlow:BlobRoot")));
        else
            builder.Services.AddSingleton<IBlobStore>(new AzureBlobStore(new BlobServiceClient(
                new Uri(Required(config, "BookFlow:BlobEndpoint")), credential)));

        var authentication = builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
        authentication.AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            if (local)
                options.TokenValidationParameters = LocalTokenValidation(config);
            else
            {
                options.Authority = Required(config, "Authentication:Authority");
                options.Audience = Required(config, "Authentication:Audience");
                options.TokenValidationParameters.NameClaimType = "oid";
                options.TokenValidationParameters.RoleClaimType = "roles";
            }
        });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("EventGrid", policy => policy.RequireAuthenticatedUser().RequireRole("EventGrid"));
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddValidation();
        builder.Services.AddBookFlowOpenApi(builder.Environment.ApplicationName);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.AllowDuplicateProperties = false;
        });

        if (outbox)
        {
            if (local)
            {
                builder.Services.AddHttpClient("local-publishing", client =>
                {
                    client.BaseAddress = new Uri(Required(config, "BookFlow:FrontendUrl"));
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
                builder.Services.AddScoped<IEventPublisher>(services =>
                    new LocalEventPublisher(services.GetRequiredService<BookFlowDbContext>(),
                        CreateLocalServiceClient(config, services.GetRequiredService<IHttpClientFactory>())));
            }
            else
                builder.Services.AddSingleton<IEventPublisher>(new AzureEventPublisher(new EventGridPublisherClient(
                    new Uri(Required(config, "BookFlow:EventGridEndpoint")), credential)));
            builder.Services.AddHostedService<OutboxWorker>();
        }
        if (consumer is not null)
        {
            if (local)
                builder.Services.AddHostedService(services => new LocalEventWorker(
                    services.GetRequiredService<IServiceScopeFactory>(), consumer,
                    services.GetRequiredService<ILogger<LocalEventWorker>>()));
            else
            {
                builder.Services.AddSingleton(new ServiceBusClient(Required(config, "BookFlow:ServiceBusNamespace"), credential));
                builder.Services.AddHostedService(services => new AzureEventWorker(
                    services.GetRequiredService<ServiceBusClient>(), services.GetRequiredService<IServiceScopeFactory>(),
                    config["BookFlow:Topic"] ?? "bookflow-events", consumer,
                    services.GetRequiredService<ILogger<AzureEventWorker>>()));
            }
        }
    }

    public static async Task UseBookFlowAsync(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        if (app.Environment.IsDevelopment())
            app.MapOpenApi();
        app.MapGet("/health/live", (CancellationToken _) => TypedResults.Ok()).AllowAnonymous()
            .WithSummary("Check process liveness")
            .Describe("GetLiveness", "Health", "Unauthenticated process probe. Returns 200 with no response body while the HTTP host is responsive.");
        app.MapGet("/health/ready", async (BookFlowDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? TypedResults.StatusCode(200) : TypedResults.StatusCode(503)).AllowAnonymous()
            .WithSummary("Check database readiness").Produces(200).Produces(503)
            .Describe("GetReadiness", "Health", "Unauthenticated database-connectivity probe. Returns an empty 200 when SQL is reachable, or an empty 503 when CanConnect reports failure. Unexpected probe exceptions use Problem Details.", 500);
        if (app.Configuration.GetValue<bool>("BookFlow:InitializeDatabase"))
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BookFlowDbContext>();
            if (app.Configuration["BookFlow:Mode"] == "Local")
                await db.Database.EnsureCreatedAsync();
            else
                await db.Database.MigrateAsync();
        }
    }

    public static Actor GetActor(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue("oid");
        var tenant = principal.FindFirstValue("tid");
        var roles = principal.FindAll("roles").Select(x => x.Value).Where(x =>
            x is "Admin" or "Author" or "Editor" or "Publisher" or "Distributor" or "PrintProvider").ToArray();
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(tenant, out var tenantId) || roles.Length != 1)
            throw new AccessDeniedException();
        return new Actor(id, tenantId, roles[0]);
    }

    public static string Required(IConfiguration config, string key) =>
        string.IsNullOrWhiteSpace(config[key])
            ? throw new InvalidOperationException($"Required configuration '{key}' is missing.")
            : config[key]!;

    public static TokenValidationParameters LocalTokenValidation(IConfiguration config) => new()
    {
        ValidateIssuer = true, ValidIssuer = "bookflow-local",
        ValidateAudience = true, ValidAudience = "bookflow",
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(15),
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(LocalSigningKey(config)),
        RoleClaimType = "roles", NameClaimType = "oid"
    };

    private static byte[] LocalSigningKey(IConfiguration config)
    {
        var key = Encoding.UTF8.GetBytes(Required(config, "Authentication:SigningKey"));
        if (key.Length < 32)
            throw new InvalidOperationException("The local signing key must be at least 32 bytes.");
        return key;
    }

    public static string LocalToken(IConfiguration config, string id, Guid tenant, string role) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "bookflow-local", "bookflow",
            [new Claim("oid", id), new Claim("tid", tenant.ToString()), new Claim("roles", role)],
            notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(LocalSigningKey(config)),
                SecurityAlgorithms.HmacSha256)));

    private static HttpClient CreateLocalServiceClient(IConfiguration config, IHttpClientFactory factory)
    {
        var client = factory.CreateClient("local-publishing");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            LocalToken(config, "eventgrid", Guid.Empty, "EventGrid"));
        return client;
    }

    public static void AddProvider(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient<ProviderHttp>(client =>
        {
            var endpoint = new Uri(Required(config, "Providers:BaseUrl"));
            if (config["BookFlow:Mode"] != "Local" && endpoint.Scheme != "https")
                throw new InvalidOperationException("Production provider endpoints must use HTTPS.");
            client.BaseAddress = endpoint;
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 20000000;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Required(config, "Providers:Token"));
        });
        services.AddScoped<IPrintProvider, HttpPrintProvider>();
        services.AddScoped<INotificationProvider, HttpNotificationProvider>();
    }
}
