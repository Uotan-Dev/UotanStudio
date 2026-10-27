using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Metadata;
namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

internal delegate bool CompressionObserver(ErofsCompressionAlgorithm algorithm,
    ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> decoded, int clusterOfs, int kernelExpectedOutputSize);

internal sealed class ErofsCompressionEstimate
{
    internal ErofsCompressionEstimate(ErofsOptions options)
        : this(options, [.. Levels(options.Algorithm)
            .Select(candidate => new Candidate(candidate.Level, candidate.HighCompression))], 0, 0)
    {
    }

    private ErofsCompressionEstimate(ErofsOptions options, Candidate[] candidates, int samples, long sampleBytes)
    {
        Options = options;
        Candidates = candidates;
        Samples = samples;
        SampleBytes = sampleBytes;
    }

    internal ErofsOptions Options { get; }
    internal int Samples { get; private set; }
    internal long SampleBytes { get; private set; }
    internal Candidate[] Candidates { get; }
    internal Candidate Inferred => Candidates.OrderByDescending(c => c.ExactMatches).ThenBy(c => c.Error).ThenBy(c => c.Level).First();
    internal Candidate Smallest => Candidates.OrderBy(c => c.Bytes).ThenBy(c => c.Level).First();

    /// <summary>Replays the candidate counters persisted at extraction so a rebuild without the source image
    /// selects exactly what the bounded source sampling inferred.
    /// <para>重放解包时持久化的候选计数，使没有源镜像的重建得到有界源采样当初推断出的同一选择。</para></summary>
    /// <param name="options">Codec options the rebuild feeds the selected level into; ownership transfers to
    /// the returned estimate. <para>重建将所选级别写入的编解码选项；所有权转移给返回的估算。</para></param>
    /// <param name="record">Persisted estimate for exactly this codec. <para>与该编解码精确对应的持久化估算。</para></param>
    /// <returns>An estimate whose candidates come from the record instead of the current level ladder.
    /// <para>返回一个候选来自记录而非当前级别阶梯的估算。</para></returns>
    internal static ErofsCompressionEstimate Restored(ErofsOptions options, ErofsRebuildCompressionEstimate record)
        => new(options, [.. record.Candidates.Select(candidate => new Candidate(candidate.Level, candidate.HighCompression)
            { Bytes = candidate.Bytes, Error = candidate.Error, ExactMatches = candidate.ExactMatches })],
            record.Samples, record.SampleBytes);

    internal void Observe(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> decoded)
    {
        var source = decoded.ToArray();
        var target = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)Options.Algorithm, source.Length)];
        foreach (var candidate in Candidates)
        {
            Options.CancellationToken.ThrowIfCancellationRequested();
            var length = ErofsCompression.AlgorithmCompress((ErofsAlgorithm)Options.Algorithm,
                source, 0, source.Length, target, 0, target.Length, candidate.Level, candidate.HighCompression,
                Options.DictionarySize, Options.WindowBits);
            if (length <= 0) throw new FileSystemCorruptedException("EROFS compression inference made no progress.");
            candidate.Bytes += length;
            candidate.Error += Math.Abs((double)length - encoded.Length) / encoded.Length;
            if (encoded.SequenceEqual(target.AsSpan(0, length))) candidate.ExactMatches++;
        }
        Samples++;
        SampleBytes += decoded.Length;
    }

    private static (int Level, bool HighCompression)[] Levels(ErofsCompressionAlgorithm algorithm) => algorithm switch
    {
        ErofsCompressionAlgorithm.Lz4 => [(1, false), (1, true), (6, true), (9, true), (12, true)],
        ErofsCompressionAlgorithm.Lzma => [(1, false), (3, false), (6, false), (9, false)],
        ErofsCompressionAlgorithm.Deflate => [(1, false), (6, false), (9, false)],
        ErofsCompressionAlgorithm.Zstd => [(1, false), (3, false), (9, false), (15, false), (19, false)],
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };

    internal sealed class Candidate(int level, bool highCompression)
    {
        internal int Level { get; } = level;
        internal bool HighCompression { get; } = highCompression;
        internal long Bytes { get; set; }
        internal double Error { get; set; }
        internal int ExactMatches { get; set; }
    }
}

/// <summary>
/// EROFS parser part that samples encoded clusters of the largest compressed inodes and compares them with local encoder candidates, yielding an estimate rather than stored encoder settings.
/// <para>EROFS 解析器的组成部分，对最大压缩 inode 的编码簇采样并与本地编码候选比较，得到的是估算结果而非文件中保存的编码器设置。</para>
/// </summary>
public sealed partial class ErofsParser
{
    /// <summary>Compares bounded source-cluster samples with local encoder candidates; estimates are not stored encoder settings.
    /// <para>把有界的源簇采样与本地编码候选比较；得到的估算不是文件中保存的编码器设置。</para></summary>
    internal List<ErofsCompressionEstimate> AnalyzeCompression(ErofsOptions options, CancellationToken token)
    {
        var estimates = options.SourceCompressionOptions!.Values
            .ToDictionary(codec => codec.Algorithm, codec => new ErofsCompressionEstimate(codec));
        foreach (var codec in options.SourceCompressionOptions.Values) codec.CancellationToken = token;
        var nids = EnumerateEntries().Where(e => !e.IsDirectory && !e.IsSymlink && !e.IsSpecialFile)
            .OrderByDescending(e => e.Size).Select(e => e.Nid).Distinct().Take(16).ToList();
        if (_superblock.PackedNid != 0) nids.Add(_superblock.PackedNid);
        foreach (var nid in nids)
        {
            token.ThrowIfCancellationRequested();
            var inode = ReadInode(nid);
            if (!inode.IsCompressed) continue;
            var samples = 0;
            try
            {
                WriteCompressedData(nid, inode, Stream.Null, (algorithm, encoded, decoded, _, _) =>
                {
                    if (estimates.TryGetValue(algorithm, out var estimate) && estimate.Samples < 16)
                        estimate.Observe(encoded, decoded);
                    return ++samples < 2;
                }, token);
            }
            catch (Exception ex) when (ex is InvalidDataException or FileSystemException or NotSupportedException)
            {
            }
        }
        return [.. estimates.Values.Where(e => e.Samples > 0).OrderBy(e => e.Options.Algorithm)];
    }
}