using System.Net.Http.Headers;
using System.Text.Json;
using BookFlow.Core;

namespace BookFlow.ApiFrontend;

public interface IFrontendService
{
    Task<HttpResponseMessage> ProxyAsync(HttpRequest request, string path, CancellationToken ct);
    Task ForwardNotificationsAsync(JsonElement[] events, string authorization, CancellationToken ct);
}

public sealed class FrontendService(IHttpClientFactory clients) : IFrontendService
{
    public async Task<HttpResponseMessage> ProxyAsync(HttpRequest request, string path, CancellationToken ct)
    {
        using var forwarded = new HttpRequestMessage(new HttpMethod(request.Method), path + request.QueryString);
        if (request.Headers.Authorization.Count > 0)
            forwarded.Headers.Authorization = AuthenticationHeaderValue.Parse(request.Headers.Authorization.ToString());
        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            forwarded.Content = new StreamContent(request.Body);
            if (request.ContentType is not null)
                forwarded.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }
        return await clients.CreateClient("webapp").SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task ForwardNotificationsAsync(JsonElement[] events, string authorization, CancellationToken ct)
    {
        var messages = events.Select(x => x.Deserialize<DomainEvent>(EventJson.Options)
            ?? throw new PermanentFailureException("Empty notification event.")).ToArray();
        foreach (var message in messages)
            BookFlow.Infrastructure.EventLookup.RequireType(message, EventTypes.Notifications);
        using var request = new HttpRequestMessage(HttpMethod.Post, "notifications")
        {
            Content = System.Net.Http.Json.JsonContent.Create(messages, options: EventJson.Options)
        };
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        using var response = await clients.CreateClient("notifications").SendAsync(request, ct);
        if ((int)response.StatusCode is >= 400 and < 500 && (int)response.StatusCode is not (408 or 429))
            throw new PermanentFailureException("Private notification ingress rejected the event.");
        response.EnsureSuccessStatusCode();
    }
}
