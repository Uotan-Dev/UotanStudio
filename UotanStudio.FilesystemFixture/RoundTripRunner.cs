using System.Text;
using System.Text.Json;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Hash.Digests;

namespace UotanStudio.FilesystemFixture;

internal static class RoundTripRunner
{
    public static async Task<int> RunAsync(
        string format,
        string source,
        string work,
        string repacked,
        string manifestPath,
        string reportPath,
        bool allowFresh,
        CancellationToken cancellationToken)
    {
        format = format.ToUpperInvariant();
        IFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            "F2FS" => new F2fsFormatHandler(),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported filesystem format.")
        };

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Source image was not found.", source);
        }

        var extraction = Path.Combine(work, "extracted-" + format.ToLowerInvariant());
        if (Directory.Exists(extraction))
        {
            Directory.Delete(extraction, recursive: true);
        }

        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(repacked))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);

        Console.WriteLine($"[managed] unpack {format} {source}");
        var unpack = await handler.UnpackAsync(source, extraction, new UnpackOptions { Overwrite = true }, cancellationToken)
            .ConfigureAwait(false);
        if (!unpack.Success)
        {
            throw new InvalidOperationException($"Unpack failed: {unpack.Summary}");
        }

        var metadataPath = FindMetadata(extraction)
            ?? throw new InvalidDataException("Unpack did not publish preservation metadata.");
        var metadataHash = HashFile(metadataPath);
        Console.WriteLine($"[managed] metadata {metadataPath} sha256={metadataHash}");

        var sourceManifest = ImageManifest.Read(source, format, cancellationToken);
        Console.WriteLine($"[managed] source entries {sourceManifest.Entries.Count}");

        IReadOnlyDictionary<string, string>? rebuildReport = null;
        var preserved = FileSystemPreservation.TryRestore(
            extraction, repacked, format, cancellationToken,
            report => rebuildReport = new Dictionary<string, string>(report, StringComparer.Ordinal));
        string packSummary;
        if (preserved)
        {
            packSummary = "preserved restore";
        }
        else if (allowFresh)
        {
            Console.WriteLine("[managed] preservation unavailable; falling back to a fresh directory build.");
            var packed = await handler.PackAsync(extraction, repacked, cancellationToken).ConfigureAwait(false);
            if (!packed.Success)
            {
                throw new InvalidOperationException($"Fresh pack failed: {packed.Summary}");
            }

            packSummary = packed.Summary;
        }
        else
        {
            throw new InvalidOperationException("Preserved restore was not available; refusing a lossy rebuild.");
        }

        var repackedManifest = ImageManifest.Read(repacked, format, cancellationToken);
        var differences = ImageManifest.Compare(sourceManifest, repackedManifest);
        var equal = differences.Count == 0;
        File.WriteAllText(manifestPath, sourceManifest.ToManifestText(), new UTF8Encoding(false));

        var report = new
        {
            format,
            source = Path.GetFullPath(source),
            sourceBytes = new FileInfo(source).Length,
            sourceSha256 = HashFile(source),
            repacked = Path.GetFullPath(repacked),
            repackedBytes = new FileInfo(repacked).Length,
            repackedSha256 = HashFile(repacked),
            preserved,
            packSummary,
            unpackSummary = unpack.Summary,
            metadataPath = Path.GetFullPath(metadataPath),
            metadataSha256 = metadataHash,
            entryCount = sourceManifest.Entries.Count,
            manifestEqual = equal,
            differences = differences.Take(50).ToArray(),
            rebuildReport,
            generatedAtUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        Console.WriteLine($"[managed] repacked entries {repackedManifest.Entries.Count}; equal={equal}");
        if (!equal)
        {
            foreach (var difference in differences.Take(20))
            {
                Console.Error.WriteLine($"[managed] difference {difference}");
            }

            return 1;
        }

        return 0;
    }

    private static string? FindMetadata(string extraction)
    {
        var resolved = RepackMetadata.ResolveActualMetadataPath(extraction);
        if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
        {
            return resolved;
        }

        return Directory.EnumerateFiles(extraction, RepackMetadata.FileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(Sha256.ComputeHash(stream));
    }
}