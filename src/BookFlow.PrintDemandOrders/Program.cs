using BookFlow.Core;
using BookFlow.Infrastructure;
using BookFlow.PrintDemandOrders;

var builder = WebApplication.CreateBuilder(args);
builder.AddBookFlow(consumer: "print-demand-orders");
builder.Services.AddProvider(builder.Configuration);
builder.Services.AddScoped<IEventHandler, PrintOrderHandler>();
var app = builder.Build();
await app.UseBookFlowAsync();
await app.RunAsync();
