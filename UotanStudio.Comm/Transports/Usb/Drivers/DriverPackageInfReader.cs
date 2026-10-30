using System.Text;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// The ownership facts one INF declares: the resolved <c>Provider</c> and the catalog file names from its
/// <c>[Version]</c> section, used to recognize the OEM INF that staging this package created.
/// <para>一个 INF 声明的归属事实：解析后的 <c>Provider</c> 与其 <c>[Version]</c> 段中的目录文件名，用于识别
/// 暂存该包时生成的 OEM INF。</para>
/// </summary>
/// <param name="Provider">The resolved provider name, for example <c>MediaTek Inc.</c>; empty when absent.
/// <para>解析后的提供者名，例如 <c>MediaTek Inc.</c>；缺失时为空。</para></param>
/// <param name="CatalogFileNames">The declared catalog file names, empty when the INF declares none.
/// <para>声明的目录文件名；INF 未声明时为空。</para></param>
internal sealed record DriverPackageInfMetadata(string Provider, IReadOnlyList<string> CatalogFileNames);

/// <summary>
/// Reads the ownership facts of a driver INF without loading it into the driver store. The text is decoded
/// from its byte order mark (UTF-16LE, UTF-8, or ANSI) and parsed with bounded size and line counts; string
/// tokens such as <c>%Provider%</c> are resolved through the INF's own <c>[Strings]</c> section.
/// <para>在不装入驱动仓库的前提下读取驱动 INF 的归属事实。文本按字节序标记（UTF-16LE、UTF-8 或 ANSI）解码，
/// 解析过程受大小与行数上限约束；<c>%Provider%</c> 这类字符串记号经该 INF 自带的 <c>[Strings]</c> 段解析。</para>
/// </summary>
internal static class DriverPackageInfReader
{
    /// <summary>The upper bound on the bytes read from one INF file.
    /// <para>单个 INF 文件可读取字节数的上限。</para></summary>
    internal const int MaximumInfBytes = 512 * 1024;

    private const int MaximumLines = 64 * 1024;
    private const int MaximumKeys = 4096;

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

            if (section is null ||
                (!section.Equals("Version", StringComparison.OrdinalIgnoreCase) &&
                    !section.Equals("Strings", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            var target = section.Equals("Version", StringComparison.OrdinalIgnoreCase) ? version : strings;
            if (target.Count >= MaximumKeys || key.Length == 0)
            {
                continue;
            }

            target.TryAdd(key, value);
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

        metadata = new DriverPackageInfMetadata(resolvedProvider, catalogs);
        return true;
    }

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