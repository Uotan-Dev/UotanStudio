using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace UotanStudio.Services.Projects;

/// <summary>Bounded ZIP transport for portable patch graphs and lazily read resources.</summary>
public static class VisualPluginBundle
{
    /// <summary>Maximum number of resources in one bundle.</summary>
    public const int MaximumAssetCount = 256;

    /// <summary>Maximum length of one resource (256 MiB).</summary>
    public const long MaximumAssetSize = 256L * 1024 * 1024;

    /// <summary>Maximum combined resource length (2 GiB).</summary>
    public const long MaximumTotalAssetSize = 2L * 1024 * 1024 * 1024;

    private const int MaximumDirectorySize = 1024 * 1024;
    private const string ManifestName = "manifest.json";

    /// <summary>Reads bounded metadata. Resource hashes are checked when resources are consumed.</summary>
    public static VisualPluginPackage Load(string path)
    {
        using var file = OpenBundle(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var manifest = archive.GetEntry(ManifestName)
            ?? throw new InvalidDataException("Patch bundle has no manifest.");
        if (manifest.Length > VisualPluginValidation.MaximumFileSize)
        {
            throw new InvalidDataException("Patch manifest exceeds its size limit.");
        }
        using var source = manifest.Open();
        var bytes = new byte[checked((int)manifest.Length)];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException("Patch manifest exceeds its declared length.");
        }
        var package = JsonSerializer.Deserialize(bytes, VisualPluginJsonContext.Default.VisualPluginPackage)
            ?? throw new InvalidDataException("Patch manifest is empty.");
        EnsureValid(package);
        ValidateEntries(archive, package);
        package.BundlePath = Path.GetFullPath(path);
        return package;
    }

    /// <summary>Streams resources into a sibling temporary ZIP and publishes only after all identities match.</summary>
    public static async Task SaveAsync(string path, VisualPluginPackage package,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(package);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(package, VisualPluginJsonContext.Default.VisualPluginPackage);
        if (manifest.LongLength > VisualPluginValidation.MaximumFileSize)
        {
            throw new InvalidDataException("Patch manifest exceeds its size limit.");
        }
        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 131072, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await using (var output = archive.CreateEntry(ManifestName, CompressionLevel.NoCompression).Open())
                    {
                        await output.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
                    }
                    foreach (var asset in package.Assets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await using var output = archive.CreateEntry("assets/" + asset.Id, CompressionLevel.NoCompression).Open();
                        await CopyAssetAsync(package, asset.Id, output, cancellationToken).ConfigureAwait(false);
                    }
                }
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Copies a resource with bounded memory and exact length/hash checks. Callers must stage the destination.</summary>
    public static async Task CopyAssetAsync(VisualPluginPackage package, string id, Stream destination,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(package);
        var asset = package.Assets.SingleOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Patch resource '{id}' does not exist.");
        if (asset.SourcePath is not null)
        {
            await using var source = new FileStream(asset.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyVerifiedAsync(source, destination, asset, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (package.BundlePath is null)
        {
            throw new InvalidDataException($"Patch resource '{id}' has no source.");
        }
        await using var file = OpenBundle(package.BundlePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        ValidateEntries(archive, package);
        await using var entry = archive.GetEntry("assets/" + id)!.Open();
        await CopyVerifiedAsync(entry, destination, asset, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyVerifiedAsync(Stream source, Stream destination, VisualPluginAsset asset,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072];
        var remaining = asset.Length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Patch resource is truncated.");
            }
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0
            || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Patch resource length or SHA-256 does not match its declared identity.");
        }
    }

    private static void EnsureValid(VisualPluginPackage package)
    {
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void ValidateEntries(ZipArchive archive, VisualPluginPackage package)
    {
        var expected = package.Assets.ToDictionary(asset => "assets/" + asset.Id, asset => asset.Length, StringComparer.Ordinal);
        expected.Add(ManifestName, -1);
        foreach (var entry in archive.Entries)
        {
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!expected.Remove(entry.FullName, out var length) || length >= 0 && length != entry.Length
                || unixType is not (0 or 0x8000))
            {
                throw new InvalidDataException("Patch bundle contains duplicate, unexpected, linked or incorrectly sized entries.");
            }
        }
        if (expected.Count != 0)
        {
            throw new InvalidDataException("Patch bundle is missing declared resources.");
        }
    }

    private static FileStream OpenBundle(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            Preflight(file);
            file.Position = 0;
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    // Validate central-directory bounds before ZipArchive allocates an object for every entry.
    // This format deliberately excludes ZIP64, multidisk archives and self-extracting prefixes.
    private static void Preflight(Stream file)
    {
        if (file.Length < 22 || file.Length > MaximumTotalAssetSize + VisualPluginValidation.MaximumFileSize + MaximumDirectorySize * 2)
        {
            throw new InvalidDataException("Patch bundle size is outside supported bounds.");
        }
        var tail = new byte[(int)Math.Min(file.Length, 65557)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054B50
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            {
                end = i;
                break;
            }
        }
        if (end < 0)
        {
            throw new InvalidDataException("Patch bundle end record is missing.");
        }
        var record = tail.AsSpan(end);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != count
            || count is 0 or > MaximumAssetCount + 1 || size > MaximumDirectorySize
            || (long)offset + size != file.Length - tail.Length + end)
        {
            throw new InvalidDataException("Patch bundle directory is oversized, ZIP64, multidisk or malformed.");
        }
        file.Position = offset;
        var directory = new byte[(int)size];
        file.ReadExactly(directory);
        var cursor = 0;
        for (var i = 0; i < count; i++)
        {
            if (directory.Length - cursor < 46)
            {
                throw new InvalidDataException("Truncated patch bundle directory.");
            }
            var header = directory.AsSpan(cursor);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50
                || (BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) & 1) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(header[42..]) >= offset)
            {
                throw new InvalidDataException("Unsupported patch bundle directory entry.");
            }
            cursor += 46 + BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (cursor > directory.Length)
            {
                throw new InvalidDataException("Patch bundle directory entry exceeds its range.");
            }
        }
        if (cursor != directory.Length)
        {
            throw new InvalidDataException("Patch bundle directory count is inconsistent.");
        }
    }
}