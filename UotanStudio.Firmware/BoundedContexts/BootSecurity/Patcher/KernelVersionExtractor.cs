using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;

/// <summary>Extracts Linux kernel release and Android KMI identifiers from kernel bytes.</summary>
public static partial class KernelVersionExtractor
{
    public static string? ExtractRelease(ReadOnlySpan<byte> data)
    {
        var text = Encoding.Latin1.GetString(data[..Math.Min(data.Length, 64 * 1024 * 1024)]);
        var match = LinuxVersionRegex().Match(text);
        if (match.Success) return match.Groups[1].Value.Trim();
        match = UtsReleaseRegex().Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static string? ExtractKmi(ReadOnlySpan<byte> data)
    {
        var release = ExtractRelease(data);
        if (string.IsNullOrWhiteSpace(release)) return null;
        var match = KmiRegex().Match(release);
        if (match.Success) return match.Groups[1].Value.Replace('_', '-');
        match = VersionRegex().Match(release);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Linux version\s+([^\x00\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinuxVersionRegex();
    [GeneratedRegex("(?:UTS_RELEASE|CONFIG_LOCALVERSION)[^\\x00\\r\\n]{0,32}[=:]\\s*[\\\"']?([0-9][^\\x00\\r\\n\\\"']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UtsReleaseRegex();
    [GeneratedRegex(@"(?i)(android\d+(?:\.\d+)?[-_]\d+\.\d+(?:[-_][0-9A-Za-z.]+)?)")]
    private static partial Regex KmiRegex();
    [GeneratedRegex(@"(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionRegex();
}