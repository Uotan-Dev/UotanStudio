using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;

internal delegate bool CompressionObserver(ErofsCompressionAlgorithm algorithm,
    ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> decoded, int clusterOfs, int kernelExpectedOutputSize);

internal sealed class ErofsCompressionEstimate(ErofsOptions options)
{
    internal ErofsOptions Options { get; } = options;
    internal int Samples { get; private set; }
    internal long SampleBytes { get; private set; }
    internal Candidate[] Candidates { get; } = Levels(options.Algorithm)
        .Select(candidate => new Candidate(candidate.Level, candidate.HighCompression)).ToArray();
    internal Candidate Inferred => Candidates.OrderByDescending(c => c.ExactMatches).ThenBy(c => c.Error).ThenBy(c => c.Level).First();
    internal Candidate Smallest => Candidates.OrderBy(c => c.Bytes).ThenBy(c => c.Level).First();

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
            if (length <= 0) throw new InvalidDataException("EROFS compression inference made no progress.");
            candidate.Bytes += length;
            candidate.Error += Math.Abs((double)length - encoded.Length) / encoded.Length;
            if (encoded.SequenceEqual(target.AsSpan(0, length))) candidate.ExactMatches++;
        }
        Samples++;
        SampleBytes += decoded.Length;
    }

    // LZ4 stores no encoder identity, so the candidates span both block encoders: the fast matcher
    // ignores the level, while LZ4HC reaches the optimal parse at 10-12.
    // LZ4 不存编码器身份，因此候选覆盖两种块编码器：快速匹配器忽略级别，LZ4HC 在 10-12 进入最优
    // 分析级。
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

public sealed partial class ErofsParser
{
    /// <summary>Compares bounded source-cluster samples with local encoder candidates; estimates are not stored encoder settings.</summary>
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
            catch (InvalidDataException)
            {
                // A source image may use a codec/window unavailable to the local
                // decoder. Parameter inheritance remains valid; simply omit its sample.
            }
            catch (NotSupportedException)
            {
                // Sampling is advisory and must never make a previously readable image
                // unrebuildable solely because a codec extension is unavailable.
            }
        }
        return estimates.Values.Where(e => e.Samples > 0).OrderBy(e => e.Options.Algorithm).ToList();
    }
}