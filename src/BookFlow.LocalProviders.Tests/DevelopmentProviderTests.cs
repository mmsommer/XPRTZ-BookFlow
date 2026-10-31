using BookFlow.Core;
using FluentAssertions;
using Moq;

namespace BookFlow.LocalProviders.Tests;

/// <summary>Tests development provider behavior.</summary>
public sealed class DevelopmentProviderTests
{
    /// <summary>Verifies synthetic provider persists idempotency across instances.</summary>
    [Fact]
    public async Task Synthetic_provider_persists_idempotency_across_instances()
    {
        var root = Path.Combine(Path.GetTempPath(), "bookflow-provider-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await new DevelopmentProvider(root).RecordAsync("key", [1, 2, 3], "order", CancellationToken.None);
            var second = await new DevelopmentProvider(root).RecordAsync("key", [1, 2, 3], "order", CancellationToken.None);
            second.Should().Be(first);
            await FluentActions.Awaiting(() => new DevelopmentProvider(root).RecordAsync("key", [3], "order", CancellationToken.None))
                .Should().ThrowAsync<ConflictException>();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>Verifies synthetic pdf has valid envelope and dependency can be mocked.</summary>
    [Fact]
    public async Task Synthetic_pdf_has_valid_envelope_and_dependency_can_be_mocked()
    {
        var provider = new Mock<IDevelopmentProvider>();
        provider.Setup(x => x.RecordAsync("key", It.IsAny<byte[]>(), "order", It.IsAny<CancellationToken>())).ReturnsAsync("reference");
        DevelopmentProvider.Pdf().Take(5).Should().Equal("%PDF-"u8.ToArray());
        System.Text.Encoding.ASCII.GetString(DevelopmentProvider.Pdf()).Should().Contain("startxref").And.Contain("%%EOF");
        (await provider.Object.RecordAsync("key", [1], "order", CancellationToken.None)).Should().Be("reference");
    }
}
