namespace UotanStudio.Firmware.Cpio.Model;

/// <summary>
/// Unified metadata abstraction for CPIO/TAR entries.
/// <para>CPIO/TAR 条目的统一元数据抽象。</para>
/// </summary>
public sealed class ArchiveEntryMetadata
{
    /// <summary>
    /// Gets the inode number when available.
    /// <para>获取可用的 inode 编号。</para>
    /// </summary>
    public ulong Inode { get; init; }

    /// <summary>
    /// Gets the raw mode value stored by the archive format.
    /// <para>获取归档格式存储的原始 mode 值。</para>
    /// </summary>
    public uint Mode { get; init; }

    /// <summary>
    /// Gets the user ID associated with the entry.
    /// <para>获取条目关联的用户 ID。</para>
    /// </summary>
    public uint UserId { get; init; }

    /// <summary>
    /// Gets the group ID associated with the entry.
    /// <para>获取条目关联的组 ID。</para>
    /// </summary>
    public uint GroupId { get; init; }

    /// <summary>
    /// Gets the major device number for the containing device.
    /// <para>获取所在设备的主设备号。</para>
    /// </summary>
    public uint DeviceMajor { get; init; }

    /// <summary>
    /// Gets the minor device number for the containing device.
    /// <para>获取所在设备的次设备号。</para>
    /// </summary>
    public uint DeviceMinor { get; init; }

    /// <summary>
    /// Gets the major device number for special file targets.
    /// <para>获取特殊文件目标的主设备号。</para>
    /// </summary>
    public uint RDeviceMajor { get; init; }

    /// <summary>
    /// Gets the minor device number for special file targets.
    /// <para>获取特殊文件目标的次设备号。</para>
    /// </summary>
    public uint RDeviceMinor { get; init; }

    /// <summary>
    /// Gets the hard-link count reported by metadata.
    /// <para>获取元数据报告的硬链接计数。</para>
    /// </summary>
    public ulong LinkCount { get; init; }

    /// <summary>
    /// Gets the Unix timestamp (seconds) for last modification time.
    /// <para>获取最后修改时间的 Unix 时间戳（秒）。</para>
    /// </summary>
    public long ModificationTimeUnixSeconds { get; init; }

    /// <summary>
    /// Gets the checksum value for formats that carry checksums.
    /// <para>获取携带校验和的格式的校验和值。</para>
    /// </summary>
    public uint Checksum { get; init; }

    /// <summary>
    /// Gets the normalized file type.
    /// <para>获取规范化的文件类型。</para>
    /// </summary>
    public CpioFileType FileType { get; init; }

    /// <summary>
    /// Gets Unix permission bits.
    /// <para>获取 Unix 权限位。</para>
    /// </summary>
    public int UnixPermissions { get; init; }

    /// <summary>
    /// Optional extended attributes keyed by xattr name.
    /// Value is raw bytes for platform-specific attribute payload.
    /// <para>按 xattr 名称索引的可选扩展属性；值为平台相关属性负载的原始字节。</para>
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> ExtendedAttributes { get; init; } = new Dictionary<string, byte[]>();
}