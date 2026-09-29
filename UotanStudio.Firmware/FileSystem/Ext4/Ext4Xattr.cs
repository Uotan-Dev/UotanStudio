namespace UotanStudio.Firmware.FileSystem.Ext4;

/// <summary>
/// Shared on-disk constants of the ext4 extended-attribute (xattr) format, used by both the parser and the builder.
/// <para>ext4 扩展属性（xattr）格式的共享磁盘常量，供解析器与构建器共同使用。</para>
/// </summary>
internal static class Ext4Xattr
{
    /// <summary>
    /// Magic at the start of an external xattr block and of the inode's inline xattr header (<c>EXT4_XATTR_MAGIC</c>).
    /// <para>外部 xattr 块与 inode 内联 xattr 头部起始处的幻数（<c>EXT4_XATTR_MAGIC</c>）。</para>
    /// </summary>
    internal const uint Magic = 0xEA020000;
}