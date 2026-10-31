using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookFlow.Core;
using Microsoft.Extensions.Logging;

namespace BookFlow.Infrastructure;

public sealed class ProviderHttp(HttpClient client, ILogger<ProviderHttp> logger)
{
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    return response;
                if (response.StatusCode != HttpStatusCode.RequestTimeout &&
                    response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new PermanentFailureException($"Provider rejected request with status {(int)status}.");
                }
                response.Dispose();
                throw new HttpRequestException("Transient provider response.");
            }
            catch (Exception ex) when (
                attempt < 2 && !ct.IsCancellationRequested &&
                ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning("Transient provider failure, attempt {Attempt}, code {Code}", attempt + 1, ex.GetType().Name);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt) + Random.Shared.Next(100)), ct);
            }
        }
    }
}

public sealed class HttpPrintProvider(ProviderHttp http) : IPrintProvider
{
    public async Task<byte[]> GenerateProofprintAsync(byte[] manuscript, string key, CancellationToken ct)
    {
        using var response = await http.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "proofprints");
            request.Headers.Add("Idempotency-Key", key);
            request.Content = new ByteArrayContent(manuscript);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return request;
        }, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (response.Content.Headers.ContentType?.MediaType != "application/pdf" ||
            bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new PermanentFailureException("Provider returned an invalid PDF.");
        return bytes;
    }

    public async Task<string> OrderAsync(PrintSubmission submission, string key, CancellationToken ct)
    {
        using var response = await http.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "orders")
            {
                Content = JsonContent.Create(submission, options: EventJson.Options)
            };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }, ct);
        var result = await response.Content.ReadFromJsonAsync<PrintProviderResponse>(EventJson.Options, ct);
        return string.IsNullOrWhiteSpace(result?.Reference)
            ? throw new PermanentFailureException("Provider omitted its order reference.")
            : result.Reference;
    }
}

/// <summary>An external print provider's stable order reference.</summary>
public sealed record PrintProviderResponse(string Reference);

public sealed class HttpNotificationProvider(ProviderHttp http) : INotificationProvider
{
    public async Task SendAsync(NotificationMessage message, string key, CancellationToken ct)
    {
        using var response = await http.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, message.PublisherApi ? "publisher-notifications" : "emails")
            {
                Content = JsonContent.Create(message, options: EventJson.Options)
            };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }, ct);
    }
}
