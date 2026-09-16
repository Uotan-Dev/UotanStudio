using System.Text.Json.Serialization;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Machine-readable mirror of the Android-style filesystem sidecars, persisted beside
/// <c>manifest.json</c> so repacking prefers structured tables over fragile flat-text parsing.
/// <para>Android 风格文件系统附属元数据的机器可读镜像，与 <c>manifest.json</c> 同目录存放，
/// 使重打包优先使用结构化表而非脆弱的扁平文本解析。</para>
/// </summary>
internal sealed class FilesystemMetadataDocument
{
    /// <summary>Gets the schema version.<para>获取模式版本。</para></summary>
    public int Version { get; set; } = 1;
    /// <summary>Gets the partition name.<para>获取分区名。</para></summary>
    public string Partition { get; set; } = "";
    /// <summary>Gets the SELinux label table keyed by image path.<para>获取以镜像路径为键的 SELinux 标签表。</para></summary>
    public Dictionary<string, string> FileContexts { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the POSIX attribute table keyed by image path.<para>获取以镜像路径为键的 POSIX 属性表。</para></summary>
    public Dictionary<string, FsConfigEntry> FsConfig { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the partition key-value table (superblock/format keys).<para>获取分区键值表（superblock/格式键）。</para></summary>
    public Dictionary<string, string> PartitionInfo { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the source rebuild parameters captured at extraction.<para>获取解包时采集的源重建参数。</para></summary>
    public RebuildParameters? Rebuild { get; set; }
}

/// <summary>
/// Parameters captured from the source image at extraction so a tree can be rebuilt without retaining
/// the original image. Physical layout is regenerated; identity is inherited where the format allows.
/// <para>解包时从源镜像采集的参数，使无需保留原镜像即可重建目录树。物理布局重新生成；
/// 在格式允许处继承镜像身份。</para>
/// </summary>
internal sealed class RebuildParameters
{
    /// <summary>Gets the source format name (<c>EXT4</c>/<c>EROFS</c>/<c>F2FS</c>).<para>获取源格式名（EXT4/EROFS/F2FS）。</para></summary>
    public string? Format { get; set; }
    /// <summary>Gets the inner filesystem format for sparse images.<para>获取稀疏镜像的内部文件系统格式。</para></summary>
    public string? InnerFormat { get; set; }
    /// <summary>Gets the filesystem block size in bytes.<para>获取文件系统块大小（字节）。</para></summary>
    public int? BlockSize { get; set; }
    /// <summary>Gets the sparse-image container block size in bytes.<para>获取稀疏镜像容器块大小（字节）。</para></summary>
    public int? SparseBlockSize { get; set; }
    /// <summary>Gets the inode size in bytes (EXT4).<para>获取 inode 大小（字节，EXT4）。</para></summary>
    public int? InodeSize { get; set; }
    /// <summary>Gets the raw filesystem UUID bytes (serialized as base64).<para>获取原始文件系统 UUID 字节（以 base64 序列化）。</para></summary>
    public byte[]? Uuid { get; set; }
    /// <summary>Gets the volume name.<para>获取卷名。</para></summary>
    public string? VolumeName { get; set; }
    /// <summary>Gets the segment size in bytes (F2FS).<para>获取段大小（字节，F2FS）。</para></summary>
    public long? SegmentSize { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FilesystemMetadataDocument))]
[JsonSerializable(typeof(RebuildParameters))]
internal partial class FilesystemMetadataJsonContext : JsonSerializerContext;