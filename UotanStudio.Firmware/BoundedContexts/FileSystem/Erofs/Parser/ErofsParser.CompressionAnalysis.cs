namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

internal delegate bool CompressionObserver(ErofsCompressionAlgorithm algorithm,
    ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> decoded, int clusterOfs, int kernelExpectedOutputSize);

internal sealed class ErofsCompressionEstimate(ErofsOptions options)
{
    internal ErofsOptions Options { get; } = options;
    internal int Samples { get; private set; }
    internal long SampleBytes { get; private set; }
    internal Candidate[] Candidates { get; } = Levels(options.Algorithm).Select(level => new Candidate(level)).ToArray();
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
                source, 0, source.Length, target, 0, target.Length, candidate.Level, Options.DictionarySize, Options.WindowBits);
            if (length <= 0) throw new InvalidDataException("EROFS compression inference made no progress.");
            candidate.Bytes += length;
            candidate.Error += Math.Abs((double)length - encoded.Length) / encoded.Length;
            if (encoded.SequenceEqual(target.AsSpan(0, length))) candidate.ExactMatches++;
        }
        Samples++;
        SampleBytes += decoded.Length;
    }

    private static int[] Levels(ErofsCompressionAlgorithm algorithm) => algorithm switch
    {
        ErofsCompressionAlgorithm.Lz4 => [1, 3, 6, 9, 12],
        ErofsCompressionAlgorithm.Lzma => [1, 3, 6, 9],
        ErofsCompressionAlgorithm.Deflate => [1, 6, 9],
        ErofsCompressionAlgorithm.Zstd => [1, 3, 9, 15, 19],
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };

    internal sealed class Candidate(int level)
    {
        internal int Level { get; } = level;
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