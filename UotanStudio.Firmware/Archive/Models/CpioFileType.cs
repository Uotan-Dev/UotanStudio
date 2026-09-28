namespace UotanStudio.Firmware.Archive.Models;

/// <summary>
/// Archive entry kind classified from the mode type bits (mask <c>0xF000</c>) and link handling.
/// <para>依据 mode 类型位（掩码 <c>0xF000</c>）与链接处理归类得到的归档条目类型。</para>
/// </summary>
public enum CpioFileType
{
    /// <summary>
    /// The mode type bits matched no known kind, or a TAR meta-entry has no file-system equivalent.
    /// <para>mode 类型位未匹配任何已知类型，或 TAR 元条目没有对应的文件系统类型。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Regular file, mode type 0x8000; also used for TAR contiguous and sparse files.
    /// <para>普通文件，mode 类型 0x8000；TAR 的连续文件与稀疏文件也归为此类。</para>
    /// </summary>
    Regular,

    /// <summary>
    /// Directory, mode type 0x4000. <para>目录，mode 类型 0x4000。</para>
    /// </summary>
    Directory,

    /// <summary>
    /// Symbolic link, mode type 0xA000; its payload holds the UTF-8 link target.
    /// <para>符号链接，mode 类型 0xA000；其负载存放 UTF-8 编码的链接目标。</para>
    /// </summary>
    SymbolicLink,

    /// <summary>
    /// Additional hard link resolved against the data-carrying entry of its inode group; no CPIO
    /// mode bit encodes it and writers emit it with regular-file type bits.
    /// <para>相对其 inode 组的数据承载条目解析出的附加硬链接；CPIO mode 位不编码该类型，写入时按普通文件的类型位输出。</para>
    /// </summary>
    HardLink,

    /// <summary>
    /// Character device, mode type 0x2000; device numbers come from the rdev fields.
    /// <para>字符设备，mode 类型 0x2000；设备号取自 rdev 字段。</para>
    /// </summary>
    CharacterDevice,

    /// <summary>
    /// Block device, mode type 0x6000; device numbers come from the rdev fields.
    /// <para>块设备，mode 类型 0x6000；设备号取自 rdev 字段。</para>
    /// </summary>
    BlockDevice,

    /// <summary>
    /// FIFO named pipe, mode type 0x1000. <para>FIFO 命名管道，mode 类型 0x1000。</para>
    /// </summary>
    Fifo,

    /// <summary>
    /// UNIX domain socket, mode type 0xC000. <para>UNIX 域套接字，mode 类型 0xC000。</para>
    /// </summary>
    Socket,
}