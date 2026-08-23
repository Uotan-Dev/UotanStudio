namespace UotanStudio.Firmware.Cpio.Model;

/// <summary>
/// Supported archive formats aligned with GNU cpio naming.
/// <para>与 GNU cpio 命名一致的支持的归档格式。</para>
/// </summary>
public enum ArchiveFormat
{
    /// <summary>
    /// Unknown or undetected archive format.
    /// <para>未知或未能识别的归档格式。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Legacy binary cpio format.
    /// <para>传统二进制 cpio 格式。</para>
    /// </summary>
    Binary,

    /// <summary>
    /// Portable old ASCII cpio format (odc).
    /// <para>可移植的旧 ASCII cpio 格式（odc）。</para>
    /// </summary>
    OldAscii,

    /// <summary>
    /// New ASCII cpio format (newc).
    /// <para>新 ASCII cpio 格式（newc）。</para>
    /// </summary>
    NewAscii,

    /// <summary>
    /// New ASCII cpio format with CRC checksum (crc).
    /// <para>带 CRC 校验和的新 ASCII cpio 格式（crc）。</para>
    /// </summary>
    CrcAscii,

    /// <summary>
    /// Tar archive format with PAX support.
    /// <para>支持 PAX 的 tar 归档格式。</para>
    /// </summary>
    Tar,

    /// <summary>
    /// USTAR tar format.
    /// <para>USTAR tar 格式。</para>
    /// </summary>
    Ustar,

    /// <summary>
    /// HP-UX old ASCII cpio variant.
    /// <para>HP-UX 旧 ASCII cpio 变体。</para>
    /// </summary>
    HpOldAscii,

    /// <summary>
    /// HP-UX binary cpio variant.
    /// <para>HP-UX 二进制 cpio 变体。</para>
    /// </summary>
    HpBinary,
}