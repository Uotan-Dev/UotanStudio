namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Specifies the type of a payload file.
/// <para>指定载荷文件的类型。</para>
/// </summary>
internal enum PayloadType
{
    /// <summary>
    /// The file type could not be determined.
    /// <para>无法确定文件类型。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The file is a raw payload.bin starting with the "CrAU" magic.
    /// <para>文件是以 "CrAU" 魔数开头的原始 payload.bin。</para>
    /// </summary>
    RawPayload = 1,

    /// <summary>
    /// The file is a ZIP archive containing a payload.bin entry.
    /// <para>文件是包含 payload.bin 条目的 ZIP 归档。</para>
    /// </summary>
    ZipArchive = 2
}