using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookFlow.Core;

namespace BookFlow.LocalProviders;

public interface IDevelopmentProvider
{
    Task<string> RecordAsync(string key, byte[] request, string operation, CancellationToken ct);
}

public sealed class DevelopmentProvider(string root) : IDevelopmentProvider
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<string> RecordAsync(string key, byte[] request, string operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 300)
            throw new ArgumentException("Idempotency-Key is required.");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + ":" + key))) + ".json");
        var hash = Convert.ToHexString(SHA256.HashData(request));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<ProviderCall>(await File.ReadAllTextAsync(path, ct))!;
                if (existing.Hash != hash)
                    throw new ConflictException("Provider idempotency key was reused.");
                return existing.Reference;
            }
            var reference = Guid.NewGuid().ToString("N");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ProviderCall(hash, reference, operation)), ct);
            File.Move(temporary, path, true);
            return reference;
        }
        finally { gate.Release(); }
    }

    private sealed record ProviderCall(string Hash, string Reference, string Operation);

    public static byte[] Pdf()
    {
        var output = new StringBuilder("%PDF-1.4\n");
        const string content = "BT /F1 16 Tf 72 720 Td (BookFlow development proofprint - test double) Tj ET\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream"
        ];
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(output.Length);
            output.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = output.Length;
        output.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            output.Append($"{offset:D10} 00000 n \n");
        output.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }
}
