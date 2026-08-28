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
}