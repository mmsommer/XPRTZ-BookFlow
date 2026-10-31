using BookFlow.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookFlow.Infrastructure.Middleware;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            AccessDeniedException => (403, "Access denied"),
            NotFoundException => (404, "Resource not found"),
            ArgumentException or PermanentFailureException => (400, "Invalid request"),
            ConflictException or DbUpdateConcurrencyException => (409, "Resource conflict"),
            HttpRequestException or TaskCanceledException => (503, "Dependency unavailable"),
            _ => (500, "Internal server error")
        };
        logger.LogError("Request {Path} failed with {Status}: {Code}", context.Request.Path, status, exception.GetType().Name);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = title, Detail = title, Instance = context.Request.Path
        }, ct);
        return true;
    }
}
