using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;
namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

public sealed class F2fsOptions
{
    public int BlockSize { get; init; } = 4096;

    public long SegmentSize { get; init; } = 2L * 1024 * 1024;

    public int SegmentCount { get; init; }

    public string VolumeName { get; init; } = "f2fs";

    public bool EnableSparse { get; init; } = true;

    public Guid? Uuid { get; init; }

    public bool EnableCompression { get; init; }

    public CompressionAlgorithm CompressionAlgorithm { get; init; } = CompressionAlgorithm.Lz4;

    public int CompressionLevel { get; init; }

    /// <summary>
    /// Optional path to an Android-style <c>fs_config</c> file (<c>path uid gid mode capabilities</c>)
    /// applied over the source tree; matching entries override the scanned uid/gid/mode.
    /// <para>可选的 Android 风格 <c>fs_config</c> 文件路径（<c>path uid gid mode capabilities</c>），
    /// 覆盖源树扫描结果：命中的条目以文件中的 uid/gid/mode 为准。</para>
    /// </summary>
    public string? FsConfigPath { get; init; }

    /// <summary>
    /// Optional path to an Android-style <c>file_contexts</c> file (<c>path label</c>) applied over
    /// the source tree; matching entries are stored as <c>security.selinux</c> extra attributes.
    /// <para>可选的 Android 风格 <c>file_contexts</c> 文件路径（<c>path label</c>），
    /// 命中的条目以 <c>security.selinux</c> 扩展属性写入镜像。</para>
    /// </summary>
    public string? FileContextsPath { get; init; }

    /// <summary>Binary Linux xattrs keyed by image-relative path; root uses an empty path.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, byte[]>>? FileXattrs { get; init; }

    /// <summary>Preloaded override tables; when set they take precedence over the path-based sources above.</summary>
    internal IReadOnlyDictionary<string, SharedKernel.FsConfigEntry>? FsConfig { get; set; }
    internal IReadOnlyDictionary<string, string>? FileContexts { get; set; }

    /// <summary>
    /// Validates and normalizes the options.
    /// <para>校验并规整选项。</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize != 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockSize), "F2FS block size must be 4096 bytes.");
        }

        if (SegmentSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentSize), "Segment size must be positive.");
        }

        if (SegmentSize / BlockSize != 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SegmentSize),
                "F2FS segment size must yield exactly 512 blocks (2 MB for 4096-byte blocks).");
        }

        if (SegmentCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentCount), "Segment count must not be negative.");
        }

        if (CompressionLevel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CompressionLevel), "Compression level must not be negative.");
        }
    }
}