using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.ProgressTracking;

var builder = WebApplication.CreateBuilder(args);
builder.AddBookFlow(consumer: "progress-tracking");
builder.Services.AddScoped<IEventHandler, ProgressHandler>();
var app = builder.Build();
await app.UseBookFlowAsync();
await app.RunAsync();
