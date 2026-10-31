using System.Net;
using BookFlow.Core;
using BookFlow.Testing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace BookFlow.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task File_blob_store_rejects_overwrite_and_path_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "bookflow-blob-" + Guid.NewGuid().ToString("N"));
        try
        {
            var blobs = new FileBlobStore(root);
            await blobs.PutAsync("manuscripts", "tenant/version.bin", new byte[] { 1, 2 }, CancellationToken.None);
            await blobs.PutAsync("manuscripts", "tenant/version.bin", new byte[] { 1, 2 }, CancellationToken.None);
            (await blobs.GetAsync("manuscripts", "tenant/version.bin", CancellationToken.None)).Should().Equal(1, 2);
            await FluentActions.Awaiting(() => blobs.PutAsync("manuscripts", "tenant/version.bin", new byte[] { 3 }, CancellationToken.None))
                .Should().ThrowAsync<ConflictException>();
            await FluentActions.Awaiting(() => blobs.GetAsync("manuscripts", "../secret", CancellationToken.None))
                .Should().ThrowAsync<ArgumentException>();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Permanent_provider_response_is_not_retried()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = new HttpClient(handler.Object) { BaseAddress = new Uri("https://provider.test/") };
        var http = new ProviderHttp(client, NullLogger<ProviderHttp>.Instance);
        await FluentActions.Awaiting(() => http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "orders"), CancellationToken.None))
            .Should().ThrowAsync<PermanentFailureException>();
        handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task Transient_provider_failure_retries_with_same_idempotency_key()
    {
        var handler = new Mock<HttpMessageHandler>();
        var keys = new List<string>();
        handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                return new HttpResponseMessage(keys.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler.Object) { BaseAddress = new Uri("https://provider.test/") };
        var http = new ProviderHttp(client, NullLogger<ProviderHttp>.Instance);
        using var response = await http.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "orders");
            request.Headers.Add("Idempotency-Key", "stable-key");
            return request;
        }, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        keys.Should().Equal("stable-key", "stable-key");
    }

    [Fact]
    public async Task Consumer_transaction_deduplicates_and_rolls_back_failure()
    {
        using var test = new TestDatabase();
        var manuscript = test.SeedManuscript();
        var message = test.Event(EventTypes.Uploaded, manuscript);
        var transaction = new EventTransaction(test.Db);
        var count = 0;
        await transaction.ExecuteAsync("test", message, _ => { count++; return Task.CompletedTask; }, CancellationToken.None);
        await transaction.ExecuteAsync("test", message, _ => { count++; return Task.CompletedTask; }, CancellationToken.None);
        count.Should().Be(1);
        test.Db.Inbox.Should().ContainSingle();
        await FluentActions.Awaiting(() => transaction.ExecuteAsync("failure", message,
            _ => throw new PermanentFailureException("Invalid"), CancellationToken.None)).Should().ThrowAsync<PermanentFailureException>();
        test.Db.Inbox.Should().ContainSingle();
    }
}
