using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookFlow.Core;
using Microsoft.Extensions.Logging;

namespace BookFlow.Infrastructure;

/// <summary>Sends provider HTTP requests with bounded retries for transient failures.</summary>
/// <param name="client">The HTTP client for provider requests or local notification forwarding.</param>
/// <param name="logger">The logger for processing and dependency failures.</param>
public sealed class ProviderHttp(HttpClient client, ILogger<ProviderHttp> logger)
{
    /// <summary>Sends a fresh request with up to two retries for transient provider failures.</summary>
    /// <param name="requestFactory">Creates a fresh HTTP request for each provider attempt.</param>
    /// <param name="ct">Cancels the operation.</param>
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

/// <summary>Calls the HTTP print provider and validates returned proofprint PDFs and order references.</summary>
/// <param name="http">The provider HTTP client with transient-failure retries.</param>
public sealed class HttpPrintProvider(ProviderHttp http) : IPrintProvider
{
    /// <summary>Generates a PDF proofprint using a stable idempotency key.</summary>
    /// <param name="manuscript">The manuscript file bytes to render.</param>
    /// <param name="key">The stable idempotency key for this operation.</param>
    /// <param name="ct">Cancels the operation.</param>
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

    /// <summary>Submits an idempotent print order and returns its provider reference.</summary>
    /// <param name="submission">The book, distributor, and copy count to print.</param>
    /// <param name="key">The stable idempotency key for this operation.</param>
    /// <param name="ct">Cancels the operation.</param>
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
/// <param name="Reference">The print provider's stable order reference.</param>
public sealed record PrintProviderResponse(string Reference);

/// <summary>Sends email and publisher notifications through the HTTP provider.</summary>
/// <param name="http">The provider HTTP client with transient-failure retries.</param>
public sealed class HttpNotificationProvider(ProviderHttp http) : INotificationProvider
{
    /// <summary>Sends a notification using a stable idempotency key.</summary>
    /// <param name="message">The notification recipient, template, and link.</param>
    /// <param name="key">The stable idempotency key for this operation.</param>
    /// <param name="ct">Cancels the operation.</param>
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
