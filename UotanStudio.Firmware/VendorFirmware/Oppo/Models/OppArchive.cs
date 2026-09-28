namespace UotanStudio.Firmware.VendorFirmware.Oppo.Models;

/// <summary>
/// A fully parsed Oppo firmware package: archive-level metadata plus the parsed file entries.
/// <para>完整解析后的 Oppo 固件包：归档级元数据及解析出的文件条目。</para>
/// </summary>
public sealed class OppArchive
{
    /// <summary>
    /// Archive-level metadata gathered during parsing.
    /// <para>解析过程中收集的归档级元数据。</para>
    /// </summary>
    /// <value>
    /// Detected format, version, entry count, project and firmware information, and any recorded key identifier.
    /// <para>检测到的格式、版本、条目数量、机型与固件信息，以及记录的密钥标识。</para>
    /// </value>
    public OppMetadata Metadata { get; init; } = new();

    /// <summary>
    /// File entries parsed from the package.
    /// <para>从归档解析出的文件条目。</para>
    /// </summary>
    /// <value>
    /// Entries in package order; an empty list when the package exposes no files.
    /// <para>按归档顺序排列；归档不含文件时为空列表。</para>
    /// </value>
    public IReadOnlyList<OppEntry> Entries { get; init; } = [];
}