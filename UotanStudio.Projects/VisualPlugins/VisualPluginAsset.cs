using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Projects;

/// <summary>Identity of one portable patch resource; source paths are local authoring state only.</summary>
public sealed class VisualPluginAsset
{
    /// <summary>Stable portable resource identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display filename, not an extraction destination.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Exact uncompressed length.</summary>
    public long Length { get; set; }

    /// <summary>SHA-256 identity in hexadecimal.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Trusted local authoring source; ignored during serialization and deserialization.</summary>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    internal VisualPluginAsset Snapshot() => new()
    {
        Id = Id,
        Name = Name,
        Length = Length,
        Sha256 = Sha256,
        SourcePath = SourcePath
    };

    /// <summary>Captures a selected file's identity without loading its contents into memory.</summary>
    public static async Task<VisualPluginAsset> FromFileAsync(string id, string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > VisualPluginBundle.MaximumAssetSize)
        {
            throw new InvalidDataException("Patch resource exceeds the per-resource size limit.");
        }
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = length;
        var buffer = new byte[131072];
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Patch resource changed while capturing its identity.");
            }
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Patch resource changed while capturing its identity.");
        }
        return new VisualPluginAsset
        {
            Id = id,
            Name = Path.GetFileName(path),
            Length = length,
            Sha256 = Convert.ToHexString(hash.GetHashAndReset()),
            SourcePath = Path.GetFullPath(path)
        };
    }
}