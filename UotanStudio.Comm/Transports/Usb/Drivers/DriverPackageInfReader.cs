using System.Text;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// The facts one INF declares: the resolved <c>Provider</c>, the catalog file names from its
/// <c>[Version]</c> section, and the hardware ids its model sections claim. The provider and catalog names
/// recognize the OEM INF that staging this package created; the hardware ids limit device re-enumeration to
/// the devices the package actually covers.
/// <para>一个 INF 声明的事实：解析后的 <c>Provider</c>、其 <c>[Version]</c> 段中的目录文件名，以及其模型段声明的
/// 硬件 ID。提供者与目录名用于识别暂存该包时生成的 OEM INF；硬件 ID 把设备重枚举限定到该包真正覆盖的设备。</para>
/// </summary>
/// <param name="Provider">The resolved provider name, for example <c>MediaTek Inc.</c>; empty when absent.
/// <para>解析后的提供者名，例如 <c>MediaTek Inc.</c>；缺失时为空。</para></param>
/// <param name="CatalogFileNames">The declared catalog file names, empty when the INF declares none.
/// <para>声明的目录文件名；INF 未声明时为空。</para></param>
/// <param name="HardwareIds">The hardware ids the model sections claim, deduplicated case-insensitively.
/// <para>模型段声明的硬件 ID，按大小写不敏感去重。</para></param>
internal sealed record DriverPackageInfMetadata(
    string Provider,
    IReadOnlyList<string> CatalogFileNames,
    IReadOnlyList<string> HardwareIds);

/// <summary>
/// Reads the facts of a driver INF without loading it into the driver store: the provider and catalog names
/// from its <c>[Version]</c> section, and the hardware ids its <c>[Manufacturer]</c>-referenced model
/// sections claim. The text is decoded from its byte order mark (UTF-16LE, UTF-8, or ANSI) and parsed with
/// bounded size and line counts; string tokens such as <c>%Provider%</c> are resolved through the INF's own
/// <c>[Strings]</c> section.
/// <para>在不装入驱动仓库的前提下读取驱动 INF 的事实：<c>[Version]</c> 段的提供者与目录文件名，以及
/// <c>[Manufacturer]</c> 引用的模型段声明的硬件 ID。文本按字节序标记（UTF-16LE、UTF-8 或 ANSI）解码，解析
/// 过程受大小与行数上限约束；<c>%Provider%</c> 这类字符串记号经该 INF 自带的 <c>[Strings]</c> 段解析。</para>
/// </summary>
internal static class DriverPackageInfReader
{
    /// <summary>The upper bound on the bytes read from one INF file.
    /// <para>单个 INF 文件可读取字节数的上限。</para></summary>
    internal const int MaximumInfBytes = 512 * 1024;

    private const int MaximumLines = 64 * 1024;
    private const int MaximumKeys = 4096;
    private const int MaximumModelLines = 16 * 1024;
    private const int MaximumHardwareIds = 1024;

    /// <summary>
    /// Reads the ownership facts of an INF file on disk.
    /// <para>读取磁盘上一个 INF 文件的归属事实。</para>
    /// </summary>
    /// <param name="infPath">Absolute path of the INF file. <para>INF 文件的绝对路径。</para></param>
    /// <param name="metadata">The parsed facts, or <see langword="null"/> on failure.
    /// <para>解析出的事实；失败时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the file was read and parsed.
    /// <para>成功读取并解析时为 <see langword="true"/>。</para></returns>
    internal static bool TryReadFile(string infPath, out DriverPackageInfMetadata? metadata)
    {
        metadata = null;
        try
        {
            using var stream = new FileStream(infPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length is <= 0 or > MaximumInfBytes)
            {
                return false;
            }

            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return TryRead(buffer, out metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the ownership facts of an INF given its bytes.
    /// <para>按字节内容读取一个 INF 的归属事实。</para>
    /// </summary>
    /// <param name="inf">The INF bytes, decoded by byte order mark. <para>INF 字节，按字节序标记解码。</para></param>
    /// <param name="metadata">The parsed facts, or <see langword="null"/> when the provider cannot be resolved.
    /// <para>解析出的事实；无法解析提供者时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when a non-empty provider was resolved.
    /// <para>解析出非空提供者时为 <see langword="true"/>。</para></returns>
    internal static bool TryRead(ReadOnlySpan<byte> inf, out DriverPackageInfMetadata? metadata)
    {
        metadata = null;
        if (inf.IsEmpty || inf.Length > MaximumInfBytes)
        {
            return false;
        }

        var version = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modelLines = new List<(string Section, string Line)>();
        string? section = null;
        var lines = 0;
        foreach (var rawLine in Decode(inf).Split('\n'))
        {
            if (++lines > MaximumLines)
            {
                break;
            }

            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (section is null)
            {
                continue;
            }

            if (section.Equals("Version", StringComparison.OrdinalIgnoreCase) ||
                section.Equals("Strings", StringComparison.OrdinalIgnoreCase))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = Unquote(line[(separator + 1)..].Trim());
                var target = section.Equals("Version", StringComparison.OrdinalIgnoreCase) ? version : strings;
                if (target.Count >= MaximumKeys || key.Length == 0)
                {
                    continue;
                }

                target.TryAdd(key, value);
            }
            else if (modelLines.Count < MaximumModelLines)
            {
                modelLines.Add((section, line));
            }
        }

        if (!version.TryGetValue("Provider", out var provider))
        {
            return false;
        }

        var resolvedProvider = Resolve(provider, strings);
        if (string.IsNullOrWhiteSpace(resolvedProvider))
        {
            return false;
        }

        var catalogs = new List<string>();
        foreach (var pair in version)
        {
            if (!pair.Key.StartsWith("CatalogFile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var catalog = Resolve(pair.Value, strings);
            if (!string.IsNullOrWhiteSpace(catalog))
            {
                catalogs.Add(catalog);
            }
        }

        metadata = new DriverPackageInfMetadata(resolvedProvider, catalogs, CollectHardwareIds(modelLines, strings));
        return true;
    }

    /// <summary>
    /// Collects the hardware ids the model sections referenced from <c>[Manufacturer]</c> declare. Only
    /// sections an entry of the manufacturer section names, decorated or not, are considered, so the ids of
    /// unrelated model sections are never attributed to this package.
    /// <para>收集 <c>[Manufacturer]</c> 引用的模型段声明的硬件 ID。只考虑制造商段条目点名的段（含平台装饰
    /// 变体），其他模型段的硬件 ID 绝不计入本包。</para>
    /// </summary>
    /// <param name="modelLines">The lines of every non-<c>Version</c>/<c>Strings</c> section.
    /// <para>除 <c>Version</c>/<c>Strings</c> 外所有段的行。</para></param>
    /// <param name="strings">The resolved <c>[Strings]</c> values. <para>已解析的 <c>[Strings]</c> 值。</para></param>
    /// <returns>The deduplicated hardware ids. <para>去重后的硬件 ID。</para></returns>
    private static List<string> CollectHardwareIds(List<(string Section, string Line)> modelLines,
        Dictionary<string, string> strings)
    {
        var bases = new List<string>();
        foreach (var (section, line) in modelLines)
        {
            if (!section.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var reference = Resolve(line[(separator + 1)..].Trim(), strings);
            var baseName = reference.Split(',')[0].Trim();
            if (baseName.Length > 0 && !baseName.StartsWith('@'))
            {
                bases.Add(baseName);
            }
        }

        if (bases.Count == 0)
        {
            return [];
        }

        var hardwareIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, line) in modelLines)
        {
            if (hardwareIds.Count >= MaximumHardwareIds)
            {
                break;
            }

            if (!IsModelSection(section, bases))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var fields = line[(separator + 1)..].Split(',');
            foreach (var field in fields.Skip(1))
            {
                var hardwareId = Resolve(field.Trim().Trim('"'), strings);
                if (hardwareId.Contains('\\'))
                {
                    hardwareIds.Add(hardwareId);
                }
            }
        }

        return [.. hardwareIds];
    }

    /// <summary>
    /// Tests whether a section is one of the manufacturer-referenced base sections or a platform-decorated
    /// variant of one.
    /// <para>判断一个段是否为制造商引用的基础段或其平台装饰变体。</para>
    /// </summary>
    /// <param name="section">The section name. <para>段名。</para></param>
    /// <param name="bases">The referenced base section names. <para>引用的基础段名。</para></param>
    /// <returns><see langword="true"/> when the section is a model section of this package.
    /// <para>该段属于本包的模型段时为 <see langword="true"/>。</para></returns>
    private static bool IsModelSection(string section, List<string> bases)
        => bases.Any(baseName => section.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
            (section.Length > baseName.Length + 1 && section.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Strips one pair of surrounding double quotes an INF string value carries, leaving unquoted values
    /// untouched.
    /// <para>剥除 INF 字符串值携带的一对外层双引号，未加引号的值原样保留。</para>
    /// </summary>
    private static string Unquote(string value)
        => value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')
            ? value[1..^1].Trim()
            : value;

    private static string Decode(ReadOnlySpan<byte> inf)
    {
        if (inf.Length >= 2 && inf[0] == 0xFF && inf[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(inf[2..]);
        }

        if (inf.Length >= 3 && inf[0] == 0xEF && inf[1] == 0xBB && inf[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(inf[3..]);
        }

        return Encoding.Latin1.GetString(inf);
    }

    private static string Resolve(string value, Dictionary<string, string> strings)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 3 || !trimmed.StartsWith('%') || !trimmed.EndsWith('%'))
        {
            return trimmed;
        }

        var key = trimmed[1..^1].Trim();
        return strings.TryGetValue(key, out var resolved) ? resolved.Trim() : string.Empty;
    }
}