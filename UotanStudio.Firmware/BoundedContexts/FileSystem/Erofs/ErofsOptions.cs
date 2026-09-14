namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed class ErofsOptions
{
    public int BlockSize { get; set; } = 4096;

    public bool Compress { get; set; } = true;

    public ErofsCompressionAlgorithm Algorithm { get; set; } = ErofsCompressionAlgorithm.Lz4;

    public int CompressionLevel { get; set; } = 1;

    /// <summary>Maximum physical compressed cluster size in bytes; zero uses one filesystem block.</summary>
    public int MaxPclusterSize { get; set; }

    /// <summary>MicroLZMA dictionary size in bytes, as declared in the compression configuration.</summary>
    public int DictionarySize { get; set; } = 8 << 20;

    /// <summary>DEFLATE or Zstandard window bits; zero selects the codec default.</summary>
    public int WindowBits { get; set; }

    /// <summary>LZ4 maximum match distance; zero denotes the upstream 65535-byte default.</summary>
    public ushort Lz4MaxDistance { get; set; }

    /// <summary>Optional source epoch and nanoseconds used by compact inodes.</summary>
    public ulong? BuildTime { get; set; }

    /// <summary>Nanoseconds associated with the filesystem epoch.</summary>
    public uint BuildTimeNanoseconds { get; set; }

    /// <summary>Build time offset from the epoch in the current EROFS superblock layout.</summary>
    public uint BuildTimeOffset { get; set; }

    /// <summary>Whether to write the upstream superblock checksum feature.</summary>
    public bool SuperblockChecksum { get; set; } = true;

    internal Dictionary<ulong, ErofsSourceInode>? SourceInodes { get; set; }
    internal Dictionary<ErofsCompressionAlgorithm, ErofsOptions>? SourceCompressionOptions { get; set; }
    internal bool SourceMtimeFeature { get; set; }
    internal byte[]? SourceVolumeNameBytes { get; set; }
    internal CancellationToken CancellationToken { get; set; }
    internal bool OptimizeCompression { get; set; }

    public bool UseCompactedIndexes { get; set; } = true;

    /// <summary>Optional filesystem UUID; extracted images retain their original identity.</summary>
    public byte[]? Uuid { get; set; }

    public string VolumeName { get; set; } = "erofs";

    public IReadOnlyDictionary<string, IReadOnlyList<ErofsXattrEntry>>? FileXattrs { get; set; }

    public bool TailPacking { get; set; } = true;

    /// <summary>Stores the final compressed cluster beside its indexes when it fits in a metadata block.</summary>
    public bool CompressedTailPacking { get; set; }

    public bool Fragments { get; set; }

    public bool Dedup { get; set; }

    public bool Metabox { get; set; }

    public int ExtraDeviceCount { get; set; }

    /// <summary>
    /// Optional path to an Android-style <c>fs_config</c> file (<c>path uid gid mode capabilities</c>)
    /// applied over the source tree; matching entries override the scanned uid/gid/mode.
    /// <para>可选的 Android 风格 <c>fs_config</c> 文件路径（<c>path uid gid mode capabilities</c>），
    /// 覆盖源树扫描结果：命中的条目以文件中的 uid/gid/mode 为准。</para>
    /// </summary>
    public string? FsConfigPath { get; set; }

    /// <summary>
    /// Optional path to an Android-style <c>file_contexts</c> file (<c>path label</c>) applied over
    /// the source tree; matching entries are stored as <c>security.selinux</c> xattrs.
    /// <para>可选的 Android 风格 <c>file_contexts</c> 文件路径（<c>path label</c>），
    /// 命中的条目以 <c>security.selinux</c> xattr 写入镜像。</para>
    /// </summary>
    public string? FileContextsPath { get; set; }

    /// <summary>
    /// Validates and normalizes the options.
    /// <para>校验并规整选项。</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize is < 512 or > 65536 || (BlockSize & (BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize),
                "BlockSize must be a power of two between 512 and 65536 bytes.");
        }

        CompressionLevel = Math.Clamp(CompressionLevel, 1, 9);
        if (!Enum.IsDefined(Algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(Algorithm), Algorithm,
                "Unknown EROFS compression algorithm.");
        }

        if (MaxPclusterSize == 0) MaxPclusterSize = BlockSize;
        if (MaxPclusterSize < BlockSize || MaxPclusterSize > ErofsCompression.MaxPclusterSize || MaxPclusterSize % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPclusterSize));
        if (DictionarySize is < 4096 or > 8 << 20)
            throw new ArgumentOutOfRangeException(nameof(DictionarySize));
        if (BuildTimeNanoseconds >= 1_000_000_000) throw new ArgumentOutOfRangeException(nameof(BuildTimeNanoseconds));
        if (Algorithm == ErofsCompressionAlgorithm.Deflate && WindowBits != 0 && WindowBits is < 8 or > 15)
            throw new ArgumentOutOfRangeException(nameof(WindowBits));
        if (Algorithm == ErofsCompressionAlgorithm.Zstd && WindowBits != 0 && WindowBits is < 10 or > 20)
            throw new ArgumentOutOfRangeException(nameof(WindowBits));
        if (Compress && UseCompactedIndexes && BlockSize > 16384)
            throw new NotSupportedException("Compacted EROFS indexes require logical clusters no larger than 16384 bytes.");

        if (string.IsNullOrEmpty(VolumeName) && SharedKernel.FileSystemEditPlan.Current is null)
        {
            VolumeName = "erofs";
        }

        if (ExtraDeviceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExtraDeviceCount), ExtraDeviceCount,
                "ExtraDeviceCount must be non-negative.");
        }


        if (Fragments)
        {
            TailPacking = false;
        }


        if (Metabox)
        {
            TailPacking = false;
        }
    }
}

internal sealed record ErofsSourceInode(ulong Mtime, uint MtimeNanoseconds, bool Extended,
    bool Compressed, ErofsCompressionAlgorithm Algorithm, bool CompactIndexes, bool InlineData, int LogicalClusterSize);