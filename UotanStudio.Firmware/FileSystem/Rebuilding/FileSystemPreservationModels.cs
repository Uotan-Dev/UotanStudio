using System.Text.Json.Serialization;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

internal sealed class PreservedInodeEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public ulong Inode { get; set; }
    public uint Flags { get; set; }
    public uint DeviceId { get; set; }
    public ushort Mode { get; set; }
    public uint Uid { get; set; }
    public uint Gid { get; set; }
    public string? SymlinkTarget { get; set; }
    public Dictionary<string, byte[]>? Xattrs { get; set; }
    public FileSystemInodeMetadata? Metadata { get; set; }
}

/// <summary>
/// Filesystem preservation payload published inside the extraction metadata file. It merges the
/// structured override tables with the verified capture record, so one JSON document carries every
/// input a preserved rebuild needs and no sibling file or directory is written.
/// <para>发布在解包元数据文件内的文件系统保留负载。它把结构化覆盖表与已校验的采集记录合并到
/// 一处，使单个 JSON 文档即可承载受保留重建所需的全部输入，不再写出兄弟文件或目录。</para>
/// </summary>
internal sealed class FileSystemPreservationData
{
    /// <summary>Gets the payload schema version.<para>获取负载模式版本。</para></summary>
    public int Version { get; set; } = 1;
    /// <summary>Gets the format name the capture was taken from.<para>获取采集自的格式名。</para></summary>
    public string Format { get; set; } = "";
    /// <summary>Gets the partition name.<para>获取分区名。</para></summary>
    public string Partition { get; set; } = "";
    /// <summary>Gets the source image length in bytes.<para>获取源镜像字节长度。</para></summary>
    public long SourceLength { get; set; }
    /// <summary>Gets the source image digest recorded at capture.<para>获取采集时记录的源镜像摘要。</para></summary>
    public string SourceHash { get; set; } = "";
    /// <summary>Gets the digest of the metadata document excluding this payload; a mismatch means the
    /// surrounding metadata was edited outside the supported edit paths.
    /// <para>获取不含本负载的元数据文档摘要；不一致表示上层元数据被受支持编辑路径之外的改动修改过。</para></summary>
    public string ConfigurationHash { get; set; } = "";
    /// <summary>Gets the serialized metadata document captured before this payload was attached.
    /// <para>获取附加本负载之前采集的元数据文档序列化结果。</para></summary>
    public string ConfigurationJson { get; set; } = "";
    /// <summary>Gets the extracted host projection keyed by relative path.<para>获取以相对路径为键的解包宿主投影。</para></summary>
    public Dictionary<string, string> Projection { get; set; } = [];
    /// <summary>Gets the aliased host path per image path for names the host cannot express directly.
    /// <para>获取宿主无法直接表达的名称所对应的镜像路径到宿主路径映射。</para></summary>
    public Dictionary<string, string> ImagePaths { get; set; } = [];
    /// <summary>Gets the partition key-value table (superblock/format keys).<para>获取分区键值表（superblock/格式键）。</para></summary>
    public Dictionary<string, string> PartitionInfo { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the POSIX attribute table keyed by image path.<para>获取以镜像路径为键的 POSIX 属性表。</para></summary>
    public Dictionary<string, FsConfigEntry> FsConfig { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the SELinux label table keyed by image path.<para>获取以镜像路径为键的 SELinux 标签表。</para></summary>
    public Dictionary<string, string> FileContexts { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets the inode entries read from the source image.<para>获取从源镜像读取的 inode 条目。</para></summary>
    public List<PreservedInodeEntry> ParsedEntries { get; set; } = [];
    /// <summary>Gets the source rebuild parameters captured at extraction.<para>获取解包时采集的源重建参数。</para></summary>
    public RebuildParameters? Rebuild { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FileSystemPreservationData))]
[JsonSerializable(typeof(RebuildParameters))]
[JsonSerializable(typeof(ErofsRebuildParameters))]
internal partial class FileSystemPreservationJsonContext : JsonSerializerContext;