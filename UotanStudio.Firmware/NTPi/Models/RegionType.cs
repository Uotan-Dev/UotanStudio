namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>指定 NTPI 固件包中区域类型的枚举。</para>
/// Specifies the type of region within an NTPI firmware package.
/// </summary>
public enum RegionType : ulong
{
    /// <summary>
    /// <para>元数据区域，扩展名为 .xml。</para>
    /// Metadata region with .xml extension.
    /// </summary>
    Metadata = 1,

    /// <summary>
    /// <para>补丁区域，扩展名为 .xml。</para>
    /// Patch region with .xml extension.
    /// </summary>
    Patch = 2,

    /// <summary>
    /// <para>原始程序区域，扩展名为 .xml。</para>
    /// RawProgram region with .xml extension.
    /// </summary>
    RawProgram = 3,

    /// <summary>
    /// <para>密钥映射区域，扩展名为 .bin。</para>
    /// KeyMap region with .bin extension.
    /// </summary>
    KeyMap = 4,

    /// <summary>
    /// <para>文件索引区域，扩展名为 .xml。</para>
    /// FileIndex region with .xml extension.
    /// </summary>
    FileIndex = 5,

    /// <summary>
    /// <para>第六区域（数据块），扩展名为 .bin。</para>
    /// Region 6 data block with .bin extension.
    /// </summary>
    Region6 = 6
}