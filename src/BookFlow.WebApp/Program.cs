using BookFlow.Infrastructure;
using BookFlow.WebApp;

var builder = WebApplication.CreateBuilder(args);
builder.AddBookFlow(outbox: true);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16000000);
builder.Services.AddScoped<IManuscriptService, ManuscriptService>();
var app = builder.Build();
await app.UseBookFlowAsync();
if (builder.Configuration.GetValue<bool>("BookFlow:InitializeOnly"))
    return;
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapManuscripts();
await app.RunAsync();
