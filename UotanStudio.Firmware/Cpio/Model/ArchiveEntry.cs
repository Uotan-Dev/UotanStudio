namespace UotanStudio.Firmware.Cpio.Model;

/// <summary>
/// Represents one entry in an archive with metadata and content bytes.
/// <para>表示归档中的一条带有元数据与内容字节的条目。</para>
/// </summary>
public sealed class ArchiveEntry
{
    /// <summary>
    /// Gets the normalized entry path within the archive.
    /// <para>获取归档内的规范化条目路径。</para>
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the link target path for symbolic and hard-link entries.
    /// <para>获取符号链接与硬链接条目的链接目标路径。</para>
    /// </summary>
    public string? LinkName { get; init; }

    /// <summary>
    /// Gets entry metadata such as file type, permissions, ownership, and timestamps.
    /// <para>获取条目元数据（文件类型、权限、属主与时间戳等）。</para>
    /// </summary>
    public required ArchiveEntryMetadata Metadata { get; init; }

    /// <summary>
    /// Gets the raw payload bytes for the entry.
    /// <para>获取条目的原始负载字节。</para>
    /// </summary>
    public required byte[] Data { get; init; }
}