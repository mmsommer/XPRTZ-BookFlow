using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using BookFlow.Core;

namespace BookFlow.Infrastructure;

/// <summary>Stores immutable file content in Azure Blob Storage.</summary>
/// <param name="client">The Azure Blob Storage service client.</param>
public sealed class AzureBlobStore(BlobServiceClient client) : IBlobStore
{
    /// <summary>Writes file content without overwriting an existing blob.</summary>
    /// <param name="container">The blob container name.</param>
    /// <param name="name">The blob name within the container.</param>
    /// <param name="content">The file bytes to store.</param>
    /// <param name="ct">Cancels the operation.</param>
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

    /// <summary>Reads the bytes of a stored blob.</summary>
    /// <param name="container">The blob container name.</param>
    /// <param name="name">The blob name within the container.</param>
    /// <param name="ct">Cancels the operation.</param>
    public async Task<byte[]> GetAsync(string container, string name, CancellationToken ct) =>
        (await client.GetBlobContainerClient(container).GetBlobClient(name).DownloadContentAsync(ct)).Value.Content.ToArray();
}

/// <summary>Stores immutable file content beneath a local directory.</summary>
/// <param name="root">The local storage root directory.</param>
public sealed class FileBlobStore(string root) : IBlobStore
{
    private string Resolve(string container, string name)
    {
        if (container is not ("manuscripts" or "proofprints") ||
            name.Split('/').Any(x => x is "" or "." or ".." || x.Contains('\\') || x.Contains(':')))
            throw new ArgumentException("Invalid blob identifier.");
        return Path.Combine(Path.GetFullPath(root), container, name.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Writes file content without overwriting an existing blob.</summary>
    /// <param name="container">The blob container name.</param>
    /// <param name="name">The blob name within the container.</param>
    /// <param name="content">The file bytes to store.</param>
    /// <param name="ct">Cancels the operation.</param>
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

    /// <summary>Reads the bytes of a stored blob.</summary>
    /// <param name="container">The blob container name.</param>
    /// <param name="name">The blob name within the container.</param>
    /// <param name="ct">Cancels the operation.</param>
    public Task<byte[]> GetAsync(string container, string name, CancellationToken ct) =>
        File.ReadAllBytesAsync(Resolve(container, name), ct);
}
