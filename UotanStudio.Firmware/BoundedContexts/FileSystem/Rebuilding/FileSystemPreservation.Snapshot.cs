using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;

public static partial class FileSystemPreservation
{
    /// <summary>
    /// Replays a captured filesystem from its metadata directory and hashed content objects.
    /// Neither the source image nor the extracted host tree is required. Output layout is regenerated.
    /// </summary>
    public static void ReplaySnapshot(string metadataDirectory, string outputPath,
        CancellationToken cancellationToken = default, long? maximumOutputBytes = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        metadataDirectory = Path.GetFullPath(metadataDirectory);
        var metadata = RepackMetadata.Read(metadataDirectory, RepackMetadataContext.LocalDirectory)
            ?? throw new InvalidDataException("Filesystem snapshot configuration is missing.");
        var id = metadata.FileSystemPreservationId;
        if (id is not { Length: 32 } || !Guid.TryParseExact(id, "N", out _))
        {
            throw new InvalidDataException("Invalid filesystem snapshot identifier.");
        }
        var directory = FileExtraction.ResolvePath(metadataDirectory, FolderName + "/" + id);
        var manifestPath = FileExtraction.ResolvePath(directory, "manifest.json");
        if (new FileInfo(manifestPath).Length > MaximumManifestBytes
            || HashFile(manifestPath, cancellationToken) != metadata.FileSystemPreservationHash)
        {
            throw new InvalidDataException("Filesystem snapshot manifest integrity check failed.");
        }
        FileSystemPreservationManifest manifest;
        using (var input = File.OpenRead(manifestPath))
        {
            manifest = JsonSerializer.Deserialize(input, FileSystemPreservationJsonContext.Default.FileSystemPreservationManifest)
                ?? throw new InvalidDataException("Invalid filesystem snapshot.");
        }
        if (manifest.Version != 2 || manifest.Format != metadata.FormatName
            || manifest.ContentObjects is null || manifest.Projection is null
            || manifest.Projection.Count > MaximumEntries)
        {
            throw new InvalidDataException("Snapshot replay requires a version 2 content-object manifest.");
        }
        var temporary = Path.Combine(Path.GetTempPath(), "uotan-filesystem-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var (path, projection) in manifest.Projection.OrderBy(pair => pair.Key.Count(c => c == '/')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = FileExtraction.ResolvePath(temporary, path);
                if (projection is null)
                {
                    throw new InvalidDataException("Null filesystem projection entry.");
                }
                if (projection == "directory" || projection.StartsWith("directory:mode=", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                if (!manifest.ContentObjects.TryGetValue(path, out var content)
                    || content is null || content.Length < 0 || content.Hash is not { Length: 64 }
                    || content.Hash.Any(character => !char.IsAsciiHexDigit(character))
                    || ProjectionIdentity(projection) != "file:" + content.Hash)
                {
                    throw new InvalidDataException($"Missing or invalid filesystem content object for '{path}'.");
                }
                var objectPath = FileExtraction.ResolvePath(directory, "objects/" + content.Hash);
                using var source = File.OpenRead(objectPath);
                if (source.Length != content.Length)
                {
                    throw new InvalidDataException($"Filesystem object length mismatch for '{path}'.");
                }
                FileExtraction.WriteAtomic(target, output =>
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long remaining = content.Length;
                    while (remaining != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (count == 0)
                        {
                            throw new EndOfStreamException("Truncated filesystem content object.");
                        }
                        output.Write(buffer, 0, count);
                        hash.AppendData(buffer, 0, count);
                        remaining -= count;
                    }
                    if (Convert.ToHexString(hash.GetHashAndReset()) != content.Hash)
                    {
                        throw new InvalidDataException($"Filesystem object hash mismatch for '{path}'.");
                    }
                });
            }
            if (!TryRestoreCore(temporary, outputPath, manifest.Format, cancellationToken, null,
                    maximumOutputBytes, null, null, metadataDirectory))
            {
                throw new InvalidDataException("Filesystem snapshot could not be replayed.");
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    internal static async Task CaptureObjectsAsync(string extraction, string directory,
        FileSystemPreservationManifest manifest, CancellationToken token)
    {
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (path, projection) in manifest.Projection)
        {
            if (!projection.StartsWith("file:", StringComparison.Ordinal))
            {
                continue;
            }
            var hash = ProjectionIdentity(projection)[5..];
            var length = new FileInfo(FileExtraction.ResolvePath(extraction, path)).Length;
            if (lengths.TryGetValue(hash, out var previous) && previous != length)
            {
                throw new InvalidDataException($"Conflicting lengths for filesystem object '{hash}'.");
            }
            lengths[hash] = length;
        }
        var counter = new ExtractionByteCounter(lengths.Values.Aggregate(0L, (sum, length) => checked(sum + length)),
            FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.Preserving), token);
        foreach (var (path, projection) in manifest.Projection)
        {
            token.ThrowIfCancellationRequested();
            if (!projection.StartsWith("file:", StringComparison.Ordinal))
            {
                continue;
            }
            var hash = ProjectionIdentity(projection)[5..];
            var sourcePath = FileExtraction.ResolvePath(extraction, path);
            var length = new FileInfo(sourcePath).Length;
            var objectPath = FileExtraction.ResolvePath(directory, "objects/" + hash);
            if (!File.Exists(objectPath))
            {
                await FileExtraction.WriteAtomicAsync(objectPath, async output =>
                {
                    await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    using var counted = counter.Wrap(output);
                    await StreamCopy.CopyExactlyAsync(input, counted, length, token).ConfigureAwait(false);
                    if (input.Length != length)
                    {
                        throw new IOException("Extracted content changed while capturing its snapshot.");
                    }
                }, overwrite: false, cancellationToken: token).ConfigureAwait(false);
                if (HashFile(objectPath, token) != hash)
                {
                    throw new IOException("Extracted content changed while capturing its snapshot.");
                }
            }
            else if (HashFile(sourcePath, token) != hash)
            {
                throw new IOException("Extracted content changed while capturing its snapshot.");
            }
            manifest.ContentObjects.Add(path, new PreservedContentObject(hash, length));
        }
        counter.Complete();
    }

    private static string ProjectionIdentity(string value)
    {
        var mode = value.IndexOf(":mode=", StringComparison.Ordinal);
        return mode < 0 ? value : value[..mode];
    }

    private static void ValidateSnapshotProjection(FileSystemPreservationManifest manifest, Dictionary<string, string> actual)
    {
        if (actual.Count != manifest.Projection.Count || manifest.Projection.Any(pair =>
                !actual.TryGetValue(pair.Key, out var value) || ProjectionIdentity(value) != ProjectionIdentity(pair.Value)))
        {
            throw new InvalidDataException("Replayed filesystem contents differ from their captured projection.");
        }
    }
}