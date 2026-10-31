using BookFlow.Core;
using BookFlow.EditorAssignment;
using BookFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddBookFlow(outbox: true, consumer: "editor-assignment");
builder.Services.AddScoped<IEditorSelectionPolicy, LeastLoadedEditorPolicy>();
builder.Services.AddScoped<IEventHandler, EditorAssignmentHandler>();
var app = builder.Build();
await app.UseBookFlowAsync();
await app.RunAsync();
