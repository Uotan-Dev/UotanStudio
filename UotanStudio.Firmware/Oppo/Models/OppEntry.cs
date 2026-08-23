namespace UotanStudio.Firmware.Oppo.Models;

/// <summary>
/// <para>表示 Oppo 固件归档中的单个条目。</para>
/// Represents a single entry in an Oppo firmware archive.
/// </summary>
public sealed class OppEntry
{
    /// <summary>
    /// <para>条目名称/路径。</para>
    /// Entry name/path.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// <para>在归档中的起始偏移量（字节）。</para>
    /// Start offset in the archive (bytes).
    /// </summary>
    internal long Offset { get; init; }

    /// <summary>
    /// <para>条目在归档中的总大小（字节）。</para>
    /// Total size of the entry in the archive (bytes).
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    /// <para>加密部分的大小（字节），0 表示不加密。</para>
    /// Size of the encrypted portion (bytes), 0 means not encrypted.
    /// </summary>
    public long EncryptedSize { get; init; }

    /// <summary>
    /// <para>是否加密。</para>
    /// Whether the entry is encrypted.
    /// </summary>
    public bool IsEncrypted => EncryptedSize > 0;

    /// <summary>
    /// <para>是否压缩。</para>
    /// Whether the entry is compressed.
    /// </summary>
    public bool IsCompressed { get; init; }

    /// <summary>
    /// <para>分区类型（如 "sahara"、"config"、"program" 等）。</para>
    /// Partition type (e.g., "sahara", "config", "program").
    /// </summary>
    public string? PartitionType { get; init; }

    /// <summary>
    /// <para>SHA256 校验值（十六进制字符串）。</para>
    /// SHA256 checksum (hex string).
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// <para>MD5 校验值（十六进制字符串）。</para>
    /// MD5 checksum (hex string).
    /// </summary>
    public string? Md5 { get; init; }

    /// <summary>
    /// <para>扇区偏移（OFP QC 格式使用）。</para>
    /// Sector offset (used by OFP QC format).
    /// </summary>
    internal long SectorOffset { get; init; }

    /// <summary>
    /// <para>扇区大小（OFP QC 格式使用）。</para>
    /// Sector size (used by OFP QC format).
    /// </summary>
    internal long SectorSize { get; init; }

    /// <summary>
    /// <para>文件名（MTK 格式的文件表条目）。</para>
    /// Filename (MTK format file table entry).
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// <para>是否为 sparse 镜像（OPS 格式 Program 分区）。</para>
    /// Whether this is a sparse image (OPS format Program partition).
    /// </summary>
    public bool IsSparse { get; init; }
}