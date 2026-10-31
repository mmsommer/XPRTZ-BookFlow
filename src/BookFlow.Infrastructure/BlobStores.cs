using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using BookFlow.Core;

namespace BookFlow.Infrastructure;

public sealed class AzureBlobStore(BlobServiceClient client) : IBlobStore
{
    public async Task PutAsync(string container, string name, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var blob = client.GetBlobContainerClient(container).GetBlobClient(name);
        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(content), overwrite: false, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            var existing = await blob.DownloadContentAsync(ct);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing.Value.Content.ToArray()), SHA256.HashData(content.Span)))
                throw new ConflictException("An immutable blob already exists with different contents.");
        }
    }

    public async Task<byte[]> GetAsync(string container, string name, CancellationToken ct) =>
        (await client.GetBlobContainerClient(container).GetBlobClient(name).DownloadContentAsync(ct)).Value.Content.ToArray();
}

public sealed class FileBlobStore(string root) : IBlobStore
{
    private string Resolve(string container, string name)
    {
        if (container is not ("manuscripts" or "proofprints") ||
            name.Split('/').Any(x => x is "" or "." or ".." || x.Contains('\\') || x.Contains(':')))
            throw new ArgumentException("Invalid blob identifier.");
        return Path.Combine(Path.GetFullPath(root), container, name.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task PutAsync(string container, string name, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var path = Resolve(container, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content.ToArray(), ct);
            try
            {
                File.Move(temporary, path, false);
            }
            catch (IOException) when (File.Exists(path))
            {
                var existing = await File.ReadAllBytesAsync(path, ct);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(content.Span)))
                    throw new ConflictException("An immutable blob already exists with different contents.");
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public Task<byte[]> GetAsync(string container, string name, CancellationToken ct) =>
        File.ReadAllBytesAsync(Resolve(container, name), ct);
}
