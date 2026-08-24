namespace UotanStudio.Firmware.Cpio.Model;

/// <summary>
/// CPIO/POSIX file type bits.
/// <para>CPIO/POSIX 文件类型位。</para>
/// </summary>
public enum CpioFileType
{
    /// <summary>
    /// Unknown or unspecified file type.
    /// <para>未知或未指定的文件类型。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Regular file.
    /// <para>普通文件。</para>
    /// </summary>
    Regular,

    /// <summary>
    /// Directory.
    /// <para>目录。</para>
    /// </summary>
    Directory,

    /// <summary>
    /// Symbolic link.
    /// <para>符号链接。</para>
    /// </summary>
    SymbolicLink,

    /// <summary>
    /// Hard link.
    /// <para>硬链接。</para>
    /// </summary>
    HardLink,

    /// <summary>
    /// Character device node.
    /// <para>字符设备节点。</para>
    /// </summary>
    CharacterDevice,

    /// <summary>
    /// Block device node.
    /// <para>块设备节点。</para>
    /// </summary>
    BlockDevice,

    /// <summary>
    /// FIFO special file.
    /// <para>FIFO 特殊文件。</para>
    /// </summary>
    Fifo,

    /// <summary>
    /// Socket special file.
    /// <para>套接字特殊文件。</para>
    /// </summary>
    Socket,
}