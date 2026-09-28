namespace UotanStudio.Firmware.Archive.Models;

/// <summary>
/// Archive container dialect selected by magic-byte detection or an explicit format hint.
/// <para>由魔数探测或显式格式提示决定的归档容器方言。</para>
/// </summary>
public enum ArchiveFormat
{
    /// <summary>Nothing has been detected or specified yet. <para>尚未检测或指定任何格式。</para></summary>
    Unknown = 0,

    /// <summary>
    /// Portable binary CPIO with the 0x71C7 magic and a 26-byte header of 16-bit fields, accepted in
    /// either byte order. <para>可移植二进制 CPIO：0x71C7 魔数、26 字节头部、16 位字段，两种字节序均可接受。</para>
    /// </summary>
    Binary,

    /// <summary>
    /// Old ASCII CPIO ("070707") whose header fields are zero-padded octal text.
    /// <para>旧 ASCII CPIO（"070707"），其头部字段为零填充的八进制文本。</para>
    /// </summary>
    OldAscii,

    /// <summary>
    /// New ASCII CPIO ("070701") with 110 bytes of 8-digit hexadecimal fields; the usual Android
    /// ramdisk layout. <para>新 ASCII CPIO（"070701"）：110 字节的 8 位十六进制字段，即常见的 Android ramdisk 布局。</para>
    /// </summary>
    NewAscii,

    /// <summary>
    /// CRC ASCII CPIO ("070702"): newc layout whose checksum field holds the payload byte sum and
    /// is verified on read. <para>CRC ASCII CPIO（"070702"）：采用 newc 布局，其校验和字段存放负载字节和，并在读取时校验。</para>
    /// </summary>
    CrcAscii,

    /// <summary>
    /// POSIX TAR without a required ustar marker; written with PAX records that carry extended attributes.
    /// <para>不要求 ustar 标记的 POSIX TAR；以携带扩展属性的 PAX 记录写出。</para>
    /// </summary>
    Tar,

    /// <summary>
    /// ustar TAR identified by the "ustar" marker at offset 257; written without PAX extension records.
    /// <para>以偏移 257 处的 "ustar" 标记识别的 ustar TAR；写出时不含 PAX 扩展记录。</para>
    /// </summary>
    Ustar,

    /// <summary>
    /// HP-UX variant of old ASCII CPIO packing the special-device major and minor into the file-size
    /// field. <para>HP-UX 版旧 ASCII CPIO：将特殊设备的主、次设备号打包进文件大小字段。</para>
    /// </summary>
    HpOldAscii,

    /// <summary>
    /// HP-UX variant of binary CPIO packing the special-device major and minor into the 16-bit size
    /// halves. <para>HP-UX 版二进制 CPIO：将特殊设备的主、次设备号打包进 16 位大小的两个半部。</para>
    /// </summary>
    HpBinary,
}