using System.Globalization;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// Replays the compression estimates captured at extraction and retains the smallest complete rebuild within
/// a bounded search of at most three whole-image builds. The source image is not available during a preserved
/// rebuild, so every input here comes from the persisted profile; without captured estimates, without a
/// recorded 64-bit extent, or without a codec configuration record the search degrades to exactly one build.
/// <para>重放解包时采集的压缩估算，并在最多三次整镜像构建的有界搜索中保留最小的完整重建。受保留重建期间无法
/// 访问源镜像，因此这里的输入全部来自持久化档案；若缺少采集的估算、缺少记录的 64 位范围，或缺少压缩配置记录，
/// 搜索退化为仅一次构建。</para>
/// </summary>
internal static class ErofsRebuildOptimizer
{
    /// <summary>Runs the bounded candidate search and publishes the smallest image into
    /// <paramref name="output"/>. Values reported are raw numbers and enum names only, so the equivalence
    /// report never needs a locale to render them.
    /// <para>执行有界候选搜索并把最小镜像写入 <paramref name="output"/>。上报值仅为原始数字与枚举名，
    /// 因此等价报告无需依赖语言环境。</para></summary>
    /// <param name="directory">Extracted tree to rebuild. <para>待重建的解包目录。</para></param>
    /// <param name="output">Stream receiving the winning image; only the fallback path and the final copy
    /// write to it. <para>接收胜出镜像的流；只有回退路径与最终拷贝会写入。</para></param>
    /// <param name="options">Options restored from the persisted profile; its codec records are re-ranked.
    /// <para>由持久化档案还原的选项；其压缩配置记录会被重新排序。</para></param>
    /// <param name="captured">Persisted profile carrying the sampled estimates and the authoritative extent.
    /// <para>携带采样估算与权威范围的持久化档案。</para></param>
    /// <param name="token">Cancels every build, copy and search step. <para>取消每次构建、拷贝与搜索步骤。</para></param>
    /// <param name="progress">Optional reporter forwarded to every candidate build, so each search attempt restarts
    /// the payload measurement instead of leaving the search silent. May be <see langword="null"/>.
    /// <para>可选的进度回调，转发给每次候选构建，使每次搜索尝试都重新开始负载计量，而不是让搜索静默。
    /// 可为 <see langword="null"/>。</para></param>
    /// <returns>The search keys (<c>CompressionAttempts</c>, <c>CompressionProfile</c>,
    /// <c>CompressionInference</c> and the size keys); an empty dictionary when the search could not run and
    /// exactly one build was published.
    /// <para>返回搜索键（<c>CompressionAttempts</c>、<c>CompressionProfile</c>、<c>CompressionInference</c>
    /// 与各体积键）；搜索无法执行而只发布一次构建时返回空字典。</para></returns>
    internal static Dictionary<string, string> Build(string directory, Stream output, ErofsOptions options,
        ErofsRebuildParameters captured, CancellationToken token, IProgress<(long Processed, long Total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(captured);
        options.CancellationToken = token;
        options.OptimizeCompression = true;
        var targetSize = captured.SourceFilesystemBytes ?? 0;
        var estimates = Rebuild(options, captured);
        if (estimates.Count == 0 || targetSize <= 0)
        {
            new ErofsBuilder().Build(directory, output, options, progress);
            return [];
        }

        foreach (var estimate in estimates) estimate.Options.OptimizeCompression = true;
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

            var encoderProfile = string.Join(", ", estimates.OrderBy(estimate => estimate.Options.Algorithm)
                .Select(estimate => $"{estimate.Options.Algorithm}:"
                    + $"{estimate.Options.CompressionLevel.ToString(CultureInfo.InvariantCulture)}"
                    + (estimate.Options.Lz4HighCompression ? "hc" : "")));
            if (!tried.Add(encoderProfile)) continue;
            candidate.SetLength(0);
            candidate.Position = 0;
            new ErofsBuilder().Build(directory, candidate, options, progress);
            token.ThrowIfCancellationRequested();
            attempts.Add($"{encoderProfile} = {candidate.Length.ToString(CultureInfo.InvariantCulture)}");
            if (candidate.Length < bestSize)
            {
                bestSize = candidate.Length;
                bestProfile = encoderProfile;
                best.SetLength(0);
                best.Position = 0;
                Copy(candidate, best, token);
            }

            if (bestSize <= targetSize) break;
        }

        Copy(best, output, token);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SourceFilesystemSize"] = targetSize.ToString(CultureInfo.InvariantCulture),
            ["RebuiltSize"] = bestSize.ToString(CultureInfo.InvariantCulture),
            ["FitsSourceFilesystemSize"] = (bestSize <= targetSize).ToString(),
            ["SizeExcess"] = Math.Max(0, bestSize - targetSize).ToString(CultureInfo.InvariantCulture),
            ["CompressionProfile"] = bestProfile,
            ["CompressionInference"] = string.Join("; ", estimates.Select(estimate =>
                $"{estimate.Options.Algorithm}:inferred={Level(estimate.Inferred)}"
                + $";smallest={Level(estimate.Smallest)}"
                + $";last={Level(estimate.Candidates[^1])}"
                + $";samples={estimate.Samples.ToString(CultureInfo.InvariantCulture)}"
                + $";sampleBytes={estimate.SampleBytes.ToString(CultureInfo.InvariantCulture)}"
                + $";exact={estimate.Inferred.ExactMatches.ToString(CultureInfo.InvariantCulture)}")),
            ["CompressionAttempts"] = string.Join("; ", attempts)
        };
    }

    /// <summary>Formats one candidate as an invariant <c>level</c> or <c>levelhc</c> token, because every
    /// reported value has to stay readable regardless of the ambient culture and carries no prose.
    /// <para>把候选格式化为不变的 <c>level</c> 或 <c>levelhc</c> 记号：所有上报值都必须在任何区域设置下
    /// 保持可读，且不携带任何文案。</para></summary>
    private static string Level(ErofsCompressionEstimate.Candidate candidate)
        => candidate.Level.ToString(CultureInfo.InvariantCulture) + (candidate.HighCompression ? "hc" : "");

    /// <summary>Binds each persisted estimate to the restored codec options of the same algorithm name.
    /// Records naming an absent codec or carrying no candidate are dropped, which is how a snapshot written
    /// by an older build degrades to a single build instead of failing.
    /// <para>把每条持久化估算绑定到同名算法的已还原编解码选项。指向缺失编解码或不含候选的记录会被丢弃，
    /// 旧版本写出的快照因此退化为单次构建而非失败。</para></summary>
    private static List<ErofsCompressionEstimate> Rebuild(ErofsOptions options, ErofsRebuildParameters captured)
    {
        if (options.SourceCompressionOptions is not { Count: > 0 } codecs) return [];
        var byName = codecs.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal);
        var estimates = new List<ErofsCompressionEstimate>();
        foreach (var record in captured.CompressionEstimates)
        {
            if (record.Candidates.Count > 0 && byName.TryGetValue(record.Algorithm, out var codec))
                estimates.Add(ErofsCompressionEstimate.Restored(codec, record));
        }

        return estimates;
    }

    private static FileStream TemporaryImage() => new(
        TempFileHelper.CreateScratchPath("uotan-erofs-candidate", ""),
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