using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.ProofprintGeneration;

var builder = WebApplication.CreateBuilder(args);
builder.AddBookFlow(outbox: true, consumer: "proofprint-generation");
builder.Services.AddProvider(builder.Configuration);
builder.Services.AddScoped<IEventHandler, ProofprintHandler>();
var app = builder.Build();
await app.UseBookFlowAsync();
await app.RunAsync();
