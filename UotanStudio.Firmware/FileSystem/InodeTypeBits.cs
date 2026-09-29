namespace UotanStudio.Firmware.FileSystem;

/// <summary>
/// POSIX <c>S_IFMT</c> file-type bits of a 16-bit inode mode, shared by the filesystem builders when
/// extracting or comparing the file-type portion of a mode value. Permission bits are not covered here.
/// <para>16 位 inode mode 中的 POSIX <c>S_IFMT</c> 文件类型位，供各文件系统构建器在提取或比较 mode 的
/// 文件类型段时共享；不包含权限位。</para>
/// </summary>
internal static class InodeTypeBits
{
    /// <summary>
    /// Format mask selecting the file-type bits within a mode (<c>S_IFMT</c>).
    /// <para>mode 中选择文件类型位的格式掩码（<c>S_IFMT</c>）。</para>
    /// </summary>
    internal const ushort FormatMask = 0xF000;

    /// <summary>
    /// FIFO / named pipe (<c>S_IFIFO</c>).
    /// <para>FIFO / 命名管道（<c>S_IFIFO</c>）。</para>
    /// </summary>
    internal const ushort Fifo = 0x1000;

    /// <summary>
    /// Character device (<c>S_IFCHR</c>).
    /// <para>字符设备（<c>S_IFCHR</c>）。</para>
    /// </summary>
    internal const ushort CharDevice = 0x2000;

    /// <summary>
    /// Directory (<c>S_IFDIR</c>).
    /// <para>目录（<c>S_IFDIR</c>）。</para>
    /// </summary>
    internal const ushort Directory = 0x4000;

    /// <summary>
    /// Block device (<c>S_IFBLK</c>).
    /// <para>块设备（<c>S_IFBLK</c>）。</para>
    /// </summary>
    internal const ushort BlockDevice = 0x6000;

    /// <summary>
    /// Regular file (<c>S_IFREG</c>).
    /// <para>常规文件（<c>S_IFREG</c>）。</para>
    /// </summary>
    internal const ushort RegularFile = 0x8000;

    /// <summary>
    /// Symbolic link (<c>S_IFLNK</c>).
    /// <para>符号链接（<c>S_IFLNK</c>）。</para>
    /// </summary>
    internal const ushort SymbolicLink = 0xA000;

    /// <summary>
    /// Socket (<c>S_IFSOCK</c>).
    /// <para>套接字（<c>S_IFSOCK</c>）。</para>
    /// </summary>
    internal const ushort Socket = 0xC000;
}