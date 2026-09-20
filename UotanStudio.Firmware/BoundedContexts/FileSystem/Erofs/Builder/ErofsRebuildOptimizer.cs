using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using System.Globalization;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;

/// <summary>Infers encoder candidates and retains the smallest complete rebuild within a bounded search.</summary>
internal static class ErofsRebuildOptimizer
{
    internal static Dictionary<string, string> Build(string directory, Stream output, ErofsParser source,
        ErofsOptions options, CancellationToken token)
    {
        options.CancellationToken = token;
        options.OptimizeCompression = true;
        foreach (var codec in options.SourceCompressionOptions!.Values) codec.OptimizeCompression = true;
        var targetSize = checked((long)source.Superblock.Blocks * options.BlockSize);
        var estimates = source.AnalyzeCompression(options, token);
        using var candidate = TemporaryImage();
        using var best = TemporaryImage();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var attempts = new List<string>();
        var bestProfile = "";
        var bestSize = long.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var estimate in estimates)
            {
                var selected = attempt switch
                {
                    0 => estimate.Inferred,
                    1 => estimate.Smallest,
                    _ => estimate.Candidates[^1],
                };
                estimate.Options.CompressionLevel = selected.Level;
                estimate.Options.Lz4HighCompression = selected.HighCompression;
            }
            if (estimates.FirstOrDefault() is { } defaultEstimate)
            {
                options.Algorithm = defaultEstimate.Options.Algorithm;
                options.CompressionLevel = defaultEstimate.Options.CompressionLevel;
                options.Lz4HighCompression = defaultEstimate.Options.Lz4HighCompression;
            }
            var profile = string.Join(", ", options.SourceCompressionOptions!.OrderBy(p => p.Key)
                .Select(p => $"{p.Key}:{p.Value.CompressionLevel}{(p.Value.Lz4HighCompression ? "hc" : "")}"));
            if (!tried.Add(profile)) continue;
            candidate.SetLength(0);
            candidate.Position = 0;
            new ErofsBuilder().Build(directory, candidate, options);
            token.ThrowIfCancellationRequested();
            attempts.Add($"{profile} = {candidate.Length.ToString(CultureInfo.InvariantCulture)} bytes");
            if (candidate.Length < bestSize)
            {
                bestSize = candidate.Length;
                bestProfile = profile;
                best.SetLength(0);
                best.Position = 0;
                Copy(candidate, best, token);
            }
            if (bestSize <= targetSize) break;
        }
        Copy(best, output, token);
        return new Dictionary<string, string>
        {
            ["SourceFilesystemSize"] = targetSize.ToString(CultureInfo.InvariantCulture),
            ["RebuiltSize"] = bestSize.ToString(CultureInfo.InvariantCulture),
            ["FitsSourceFilesystemSize"] = (bestSize <= targetSize).ToString(),
            ["SizeExcess"] = Math.Max(0, bestSize - targetSize).ToString(CultureInfo.InvariantCulture),
            ["CompressionProfile"] = bestProfile,
            ["CompressionInference"] = string.Join("; ", estimates.Select(e =>
                $"{e.Options.Algorithm}: candidate level {e.Inferred.Level} ({(e.Inferred.HighCompression ? "LZ4HC" : "fast LZ4")}), {e.Samples} samples, {e.SampleBytes} decoded bytes, {e.Inferred.ExactMatches} exact re-encodings; the source encoder identity is only inferred from byte-identical re-encodings")),
            ["CompressionAttempts"] = string.Join("; ", attempts)
        };
    }

    private static FileStream TemporaryImage() => new(
        Path.Combine(Path.GetTempPath(), "uotan-erofs-candidate-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);

    private static void Copy(Stream source, Stream destination, CancellationToken token)
    {
        source.Position = 0;
        var buffer = new byte[65536];
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, count);
        }
    }
}