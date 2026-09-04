namespace UotanStudio.Firmware.Ext4;

/// <summary>
/// Packaging options for <see cref="Ext4Builder"/>.
/// <para><see cref="Ext4Builder"/> 的打包选项。</para>
/// </summary>
public sealed class Ext4Options
{
    /// <summary>
    /// Filesystem block size in bytes.
    /// <para>文件系统块大小（字节）。</para>
    /// </summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>
    /// Inode size in bytes.
    /// <para>inode 大小（字节）。</para>
    /// </summary>
    public int InodeSize { get; init; } = 256;

    /// <summary>
    /// Reserved switch for future ext4 compression support (not implemented).
    /// <para>为未来 ext4 压缩支持预留的开关（未实现）。</para>
    /// </summary>
    public bool Ext4Compress { get; set; } = false;

    /// <summary>
    /// When enabled, all-zero file blocks are emitted as extents holes instead of
    /// allocated data blocks (sparse file support).
    /// <para>启用后，全零文件块以 extent 空洞而非已分配数据块写入（稀疏文件支持）。</para>
    /// </summary>
    public bool EnableSparse { get; set; } = false;

    /// <summary>
    /// Filesystem UUID (16 bytes). When null, a random UUID is generated.
    /// <para>文件系统 UUID（16 字节）。为 null 时生成随机 UUID。</para>
    /// </summary>
    public byte[]? Uuid { get; init; }

    /// <summary>
    /// Whether to preserve file permissions (mode, uid, gid) from the source directory.
    /// When false, fixed defaults (0644/0755, uid=gid=0) are used.
    /// <para>是否从源目录继承文件权限（mode/uid/gid）。为 false 时使用固定默认值。</para>
    /// </summary>
    public bool PreservePermissions { get; set; } = true;

    /// <summary>
    /// Whether to preserve file timestamps (mtime, ctime) from the source directory.
    /// When false, a fixed timestamp is used.
    /// <para>是否从源目录继承文件时间戳（mtime/ctime）。为 false 时使用固定时间戳。</para>
    /// </summary>
    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>
    /// Whether to enable ext4 metadata checksums (EXT4_FEATURE_RO_COMPAT_METADATA_CSUM).
    /// When enabled, per-inode CRC32c checksums are written and the filesystem advertises the
    /// metadata_csum read-only-compatible feature. When disabled, no inode checksums are written
    /// and the feature bit is cleared.
    /// <para>是否启用 ext4 元数据校验和（EXT4_FEATURE_RO_COMPAT_METADATA_CSUM）。
    /// 启用时写入每个 inode 的 CRC32c 校验和并在文件系统上声明 metadata_csum 只读兼容特性；
    /// 禁用时不写 inode 校验和并清除该特性位。</para>
    /// </summary>
    public bool EnableMetadataCsum { get; set; } = true;

    /// <summary>
    /// Whether to include an internal journal (EXT4_FEATURE_COMPAT_HAS_JOURNAL).
    /// When enabled (the default, mirroring mke2fs), a JBD2 journal superblock plus a clean empty
    /// log area are allocated inside the image, journal inode #8 is reserved and the superblock
    /// advertises <c>has_journal</c> with <c>s_journal_inum = 8</c>. When disabled, no journal area
    /// is reserved, inode #8 stays free and the <c>has_journal</c> feature bit is not set.
    /// <para>是否构建内部日志（EXT4_FEATURE_COMPAT_HAS_JOURNAL）。启用时（默认，对齐 mke2fs），
    /// 在镜像内预留 JBD2 日志超级块与干净空日志区，保留日志 inode 8 号并在超级块声明
    /// <c>has_journal</c> 且 <c>s_journal_inum = 8</c>；禁用时不预留日志区，inode 8 保持空闲且
    /// 不置 <c>has_journal</c> 特性位。</para>
    /// </summary>
    public bool IncludeJournal { get; set; } = true;
}