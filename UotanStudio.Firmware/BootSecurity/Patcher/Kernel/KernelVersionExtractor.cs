using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BootSecurity.Patcher.Kernel;

/// <summary>Extracts Linux kernel release and Android KMI identifiers from kernel bytes.
/// <para>从内核字节中提取 Linux 内核版本号与 Android KMI 标识。</para></summary>
internal static partial class KernelVersionExtractor
{
    /// <summary>
    /// Reads the kernel release string from the first 64 MiB of <paramref name="data"/>, preferring a
    /// "Linux version" banner and falling back to UTS_RELEASE/CONFIG_LOCALVERSION tokens.
    /// <para>从 <paramref name="data"/> 的前 64 MiB 中读取内核版本号，优先匹配 “Linux version” 横幅，
    /// 未命中时回退到 UTS_RELEASE/CONFIG_LOCALVERSION 标记。</para>
    /// </summary>
    /// <param name="data">
    /// Raw kernel bytes, compressed or decompressed, interpreted as Latin-1 text.
    /// <para>按 Latin-1 文本解释的原始内核字节，可以是压缩或未压缩的。</para>
    /// </param>
    /// <returns>The trimmed release string, or <see langword="null"/> when neither pattern matches.
    /// <para>去除首尾空白后的版本号；两种模式均未匹配时返回 <see langword="null"/>。</para></returns>
    public static string? ExtractRelease(ReadOnlySpan<byte> data)
    {
        var text = Encoding.Latin1.GetString(data[..Math.Min(data.Length, 64 * 1024 * 1024)]);
        var match = LinuxVersionRegex().Match(text);
        if (match.Success) return match.Groups[1].Value.Trim();
        match = UtsReleaseRegex().Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Derives the Android KMI identifier from the extracted kernel release, normalizing underscores to hyphens.
    /// <para>从提取到的内核版本号推导 Android KMI 标识，并把下划线归一化为连字符。</para>
    /// </summary>
    /// <param name="data">Raw kernel bytes passed to <see cref="ExtractRelease"/>.
    /// <para>交由 <see cref="ExtractRelease"/> 处理的原始内核字节。</para></param>
    /// <returns>An <c>androidNN-x.y</c>-style identifier, a bare <c>x.y[.z]</c> version when the release
    /// carries no Android tag, or <see langword="null"/> when no release can be extracted.
    /// <para><c>androidNN-x.y</c> 形式的标识；版本号不含 Android 标记时为纯 <c>x.y[.z]</c> 版本；
    /// 无法提取版本号时返回 <see langword="null"/>。</para></returns>
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