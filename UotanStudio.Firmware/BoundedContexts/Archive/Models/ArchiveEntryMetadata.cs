namespace UotanStudio.Firmware.BoundedContexts.Archive.Models;

/// <summary>
/// Format-neutral ownership, permission, device and timestamp metadata captured for one archive entry.
/// <para>为单个归档条目捕获的与格式无关的归属、权限、设备号与时间戳元数据。</para>
/// </summary>
public sealed class ArchiveEntryMetadata
{
    /// <summary>
    /// Inode number as stored in the archive header (newc <c>c_ino</c>, old ASCII/binary <c>c_ino</c>).
    /// Entries sharing one non-zero (Inode, DeviceMajor, DeviceMinor) triple form a hardlink group.
    /// <para>归档头部中存储的 inode 编号（newc 的 <c>c_ino</c>、旧 ASCII/二进制格式的 <c>c_ino</c>）。
    /// (Inode, DeviceMajor, DeviceMinor) 三元组相同的非零条目构成一个硬链接组。</para>
    /// </summary>
    /// <value>
    /// The header inode value; 0 when the source format carries no usable inode.
    /// <para>头部中的 inode 值；源格式未提供有效 inode 时为 0。</para>
    /// </value>
    public ulong Inode { get; init; }

    /// <summary>
    /// Raw mode word combining the file type bits (mask <c>0xF000</c>) and the permission bits
    /// (mask <c>0x0FFF</c>), mirroring <c>st_mode</c>.
    /// <para>合并了文件类型位（掩码 <c>0xF000</c>）与权限位（掩码 <c>0x0FFF</c>）的原始 mode 字，与 <c>st_mode</c> 对应。</para>
    /// </summary>
    /// <value>
    /// The mode field exactly as read; the CPIO writer masks it per format width.
    /// <para>按读取原样的 mode 字段；CPIO 写入器按各格式的字段宽度进行掩码。</para>
    /// </value>
    public uint Mode { get; init; }

    /// <summary>
    /// Owning user identifier (<c>c_uid</c> in CPIO, the TAR owner uid).
    /// <para>所属用户标识符（CPIO 中的 <c>c_uid</c>，TAR 的属主 uid）。</para>
    /// </summary>
    /// <value>
    /// The uid stored in the header; 0 conventionally denotes root.
    /// <para>头部中存储的 uid；按惯例 0 表示 root。</para>
    /// </value>
    public uint UserId { get; init; }

    /// <summary>
    /// Owning group identifier (<c>c_gid</c> in CPIO, the TAR owner gid).
    /// <para>所属组标识符（CPIO 中的 <c>c_gid</c>，TAR 的属主 gid）。</para>
    /// </summary>
    /// <value>
    /// The gid stored in the header; 0 conventionally denotes the root group.
    /// <para>头部中存储的 gid；按惯例 0 表示 root 组。</para>
    /// </value>
    public uint GroupId { get; init; }

    /// <summary>
    /// Major number of the device on which the entry resides (<c>c_dev_major</c> in newc).
    /// <para>条目所在设备的主设备号（newc 中的 <c>c_dev_major</c>）。</para>
    /// </summary>
    /// <value>
    /// The device major; old ASCII and binary formats store a single <c>c_dev</c> here and leave
    /// DeviceMinor at 0. <para>主设备号；旧 ASCII 与二进制格式将单个 <c>c_dev</c> 存于此，并使 DeviceMinor 保持 0。</para>
    /// </value>
    public uint DeviceMajor { get; init; }

    /// <summary>
    /// Minor number of the device on which the entry resides (<c>c_dev_minor</c> in newc).
    /// <para>条目所在设备的次设备号（newc 中的 <c>c_dev_minor</c>）。</para>
    /// </summary>
    /// <value>
    /// The device minor; 0 when the source format has no separate minor field.
    /// <para>次设备号；源格式没有独立次字段时为 0。</para>
    /// </value>
    public uint DeviceMinor { get; init; }

    /// <summary>
    /// Major device number of the character or block device the entry represents (<c>c_rdev_major</c> in newc).
    /// <para>条目所表示的字符或块设备的主设备号（newc 中的 <c>c_rdev_major</c>）。</para>
    /// </summary>
    /// <value>
    /// The rdev major; for HP formats the reader recovers it from the packed file-size field of
    /// special devices. <para>rdev 主号；HP 格式下读取器从特殊设备打包的文件大小字段中恢复该值。</para>
    /// </value>
    public uint RDeviceMajor { get; init; }

    /// <summary>
    /// Minor device number of the character or block device the entry represents (<c>c_rdev_minor</c> in newc).
    /// <para>条目所表示的字符或块设备的次设备号（newc 中的 <c>c_rdev_minor</c>）。</para>
    /// </summary>
    /// <value>
    /// The rdev minor; 0 for non-device entries and for formats without a separate minor field.
    /// <para>rdev 次号；非设备条目以及没有独立次字段的格式为 0。</para>
    /// </value>
    public uint RDeviceMinor { get; init; }

    /// <summary>
    /// Hard link count (<c>c_nlink</c>); newc readers group entries only when this exceeds 1.
    /// <para>硬链接计数（<c>c_nlink</c>）；newc 读取器仅在该值大于 1 时对条目进行归组。</para>
    /// </summary>
    /// <value>
    /// The link count from the header; a CPIO writer emits 1 when this is 0.
    /// <para>头部中的链接数；该值为 0 时 CPIO 写入器写出 1。</para>
    /// </value>
    public ulong LinkCount { get; init; }

    /// <summary>
    /// Modification time as whole seconds since the Unix epoch (<c>c_mtime</c>, the TAR mtime record).
    /// <para>以 Unix 历元起的整秒表示的修改时间（<c>c_mtime</c>，TAR 的 mtime 记录）。</para>
    /// </summary>
    /// <value>
    /// Signed seconds; a negative value is clamped to 0 when writing, and TAR nanosecond precision is truncated.
    /// <para>带符号秒数；写入时负值被钳制为 0，TAR 的纳秒精度会被截断。</para>
    /// </value>
    public long ModificationTimeUnixSeconds { get; init; }

    /// <summary>
    /// Header checksum field; meaningful only for the CRC ASCII (070702) format.
    /// <para>头部校验和字段；仅对 CRC ASCII（070702）格式有意义。</para>
    /// </summary>
    /// <value>
    /// The byte sum of the payload modulo 2^32, verified during reading; 0 for all other formats.
    /// <para>负载字节和在 2^32 下取模，读取时会进行校验；其他所有格式为 0。</para>
    /// </value>
    public uint Checksum { get; init; }

    /// <summary>
    /// Entry kind classified from the mode type bits.
    /// <para>依据 mode 类型位归类得到的条目类型。</para>
    /// </summary>
    /// <value>
    /// The classified type; <see cref="CpioFileType.HardLink"/> is synthesized by link consolidation and
    /// never decoded from mode bits. <para>归类后的类型；<see cref="CpioFileType.HardLink"/> 由链接归组合成，绝不从 mode 位解码得到。</para>
    /// </value>
    public CpioFileType FileType { get; init; }

    /// <summary>
    /// Permission bits (<c>Mode</c> &amp; <c>0x0FFF</c>) in the Unix 0777 sense.
    /// <para>Unix 0777 意义上的权限位（<c>Mode</c> &amp; <c>0x0FFF</c>）。</para>
    /// </summary>
    /// <value>
    /// The low 12 bits of the mode, including setuid/setgid/sticky; the TAR writer maps them onto
    /// <c>UnixFileMode</c>. <para>mode 的低 12 位，含 setuid/setgid/sticky；TAR 写入器将其映射到 <c>UnixFileMode</c>。</para>
    /// </value>
    public int UnixPermissions { get; init; }

    /// <summary>
    /// Extended attributes keyed by name with raw value bytes; populated only by the TAR reader.
    /// <para>以名称为键、值为原始字节的扩展属性；仅由 TAR 读取器填充。</para>
    /// </summary>
    /// <value>
    /// Attributes decoded from PAX records prefixed with <c>SCHILY.xattr.</c> or
    /// <c>LIBARCHIVE.xattr.</c>; an empty dictionary by default and for every CPIO entry.
    /// <para>由带 <c>SCHILY.xattr.</c> 或 <c>LIBARCHIVE.xattr.</c> 前缀的 PAX 记录解码而来；默认以及所有 CPIO 条目为空字典。</para>
    /// </value>
    public IReadOnlyDictionary<string, byte[]> ExtendedAttributes { get; init; } = new Dictionary<string, byte[]>();
}