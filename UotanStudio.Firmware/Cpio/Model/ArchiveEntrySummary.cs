namespace UotanStudio.Firmware.Cpio.Model;

/// <summary>
/// Lightweight archive entry metadata for listing and verification scenarios.
/// <para>用于列表与校验场景的轻量级归档条目元数据。</para>
/// </summary>
public sealed class ArchiveEntrySummary
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
    /// Gets the payload size in bytes.
    /// <para>获取负载的字节大小。</para>
    /// </summary>
    public long DataLength { get; init; }
}