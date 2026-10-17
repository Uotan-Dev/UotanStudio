namespace UotanStudio.Firmware.VendorFirmware.Oppo.Models;

/// <summary>
/// Options controlling how an Oppo archive is extracted.
/// <para>控制 Oppo 归档提取方式的选项。</para>
/// </summary>
public sealed class OppExtractOptions
{
    /// <summary>
    /// Whether existing output files are replaced during extraction.
    /// <para>提取时是否覆盖已存在的输出文件。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> replaces the existing file; <see langword="false"/> skips the entry and leaves the existing file untouched.
    /// <para>为 <see langword="true"/> 时覆盖已存在的输出文件；为 <see langword="false"/> 时跳过该条目并保留现有文件。</para>
    /// </value>
    public bool Overwrite { get; init; }

    /// <summary>
    /// Identifier of the decryption key to use, passed to the format parser.
    /// <para>要使用的解密密钥标识，传递给格式解析器。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> lets extraction fall back to the key recorded in archive metadata or auto-detected while decrypting.
    /// <para>为 <see langword="null"/> 时提取回退到归档元数据记录或解密时自动检测到的密钥。</para>
    /// </value>
    public string? KeyId { get; init; }

    /// <summary>Exact archive entry names to extract; null extracts every entry.
    /// <para>要提取的归档条目全名；为 null 时提取全部条目。</para></summary>
    /// <value>
    /// Exact archive entry names to extract, matched case-insensitively; <see langword="null"/> extracts every entry. A name matching no archive entry makes extraction fail with <see cref="Exceptions.InvalidOppFormatException"/>.
    /// <para>要提取的归档条目全名，匹配时不区分大小写；为 <see langword="null"/> 时提取全部条目。若某个名称与任何归档条目都不匹配，提取将以 <see cref="Exceptions.InvalidOppFormatException"/> 失败。</para>
    /// </value>
    public IReadOnlyCollection<string>? SelectedEntries { get; init; }
}