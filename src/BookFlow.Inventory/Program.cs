using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.Inventory;

var builder = WebApplication.CreateBuilder(args);
builder.AddBookFlow(consumer: "inventory");
builder.Services.AddScoped<IEventHandler, InventoryHandler>();
var app = builder.Build();
await app.UseBookFlowAsync();
await app.RunAsync();
