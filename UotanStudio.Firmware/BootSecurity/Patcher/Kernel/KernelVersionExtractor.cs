using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BootSecurity.Patcher.Kernel;

/// <summary>Extracts Linux kernel release and Android KMI identifiers from kernel bytes.
/// <para>从内核字节中提取 Linux 内核版本号与 Android KMI 标识。</para></summary>
/// <remarks>
/// The banner scan works directly on the Latin-1 bytes so a 64 MiB kernel is never materialized as a
/// 128 MiB UTF-16 string; only the matched release token is decoded.
/// <para>横幅扫描直接在 Latin-1 字节上进行，64 MiB 内核不会被物化为 128 MiB 的 UTF-16 字符串；只解码命中的版本标记。</para>
/// </remarks>
internal static partial class KernelVersionExtractor
{
    private const int MaximumScanLength = 64 * 1024 * 1024;
    private static ReadOnlySpan<byte> LinuxVersionToken => "Linux version"u8;
    private static ReadOnlySpan<byte> UtsReleaseToken => "UTS_RELEASE"u8;
    private static ReadOnlySpan<byte> ConfigLocalVersionToken => "CONFIG_LOCALVERSION"u8;
    private static readonly SearchValues<byte> LinuxVersionFirstBytes = SearchValues.Create("lL"u8);
    private static readonly SearchValues<byte> UtsReleaseFirstBytes = SearchValues.Create("uUcC"u8);

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
        var window = data.Length > MaximumScanLength ? data[..MaximumScanLength] : data;
        return ExtractLinuxVersion(window) ?? ExtractUtsRelease(window);
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

    private static string? ExtractLinuxVersion(ReadOnlySpan<byte> text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var index = FindAsciiIgnoreCase(text, pos, LinuxVersionToken, LinuxVersionFirstBytes);
            if (index < 0)
                return null;
            var valueStart = index + LinuxVersionToken.Length;
            var start = valueStart;
            while (valueStart < text.Length && IsLatin1Whitespace(text[valueStart]))
                valueStart++;
            if (valueStart == start)
            {
                pos = index + 1;
                continue;
            }
            var end = valueStart;
            while (end < text.Length && text[end] != 0 && !IsLatin1Whitespace(text[end]))
                end++;
            if (end == valueStart)
            {
                pos = index + 1;
                continue;
            }
            return Encoding.Latin1.GetString(text[valueStart..end]);
        }
        return null;
    }

    private static string? ExtractUtsRelease(ReadOnlySpan<byte> text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var index = FindAsciiIgnoreCase(text, pos, UtsReleaseToken, UtsReleaseFirstBytes);
            var configIndex = FindAsciiIgnoreCase(text, pos, ConfigLocalVersionToken, UtsReleaseFirstBytes);
            var tokenLength = UtsReleaseToken.Length;
            if (configIndex >= 0 && (index < 0 || configIndex < index))
            {
                index = configIndex;
                tokenLength = ConfigLocalVersionToken.Length;
            }
            if (index < 0)
                return null;
            pos = index + 1;
            var value = TryMatchUtsSuffix(text, index + tokenLength);
            if (value is not null)
                return value;
        }
        return null;
    }

    private static string? TryMatchUtsSuffix(ReadOnlySpan<byte> text, int tokenEnd)
    {
        var horizon = tokenEnd;
        while (horizon < text.Length && text[horizon] is not (0 or (byte)'\r' or (byte)'\n'))
            horizon++;
        var separator = Math.Min(tokenEnd + 32, horizon - 1);
        if (separator >= text.Length)
            separator = text.Length - 1;
        for (; separator >= tokenEnd; separator--)
        {
            if (text[separator] is not ((byte)'=' or (byte)':'))
                continue;
            var position = separator + 1;
            while (position < text.Length && IsLatin1Whitespace(text[position]))
                position++;
            if (position < text.Length && text[position] is (byte)'"' or (byte)'\'')
                position++;
            if (position >= text.Length || text[position] is < (byte)'0' or > (byte)'9')
                continue;
            var start = position;
            position++;
            while (position < text.Length && text[position] is not (0 or (byte)'\r' or (byte)'\n' or (byte)'"' or (byte)'\''))
                position++;
            if (position - start < 2)
                continue;
            return Encoding.Latin1.GetString(text[start..position]).Trim();
        }
        return null;
    }

    private static int FindAsciiIgnoreCase(ReadOnlySpan<byte> text, int start, ReadOnlySpan<byte> value,
        SearchValues<byte> firstBytes)
    {
        var pos = start;
        while (pos <= text.Length - value.Length)
        {
            var relative = text[pos..].IndexOfAny(firstBytes);
            if (relative < 0)
                return -1;
            var index = pos + relative;
            if (index > text.Length - value.Length)
                return -1;
            if (StartsWithAsciiIgnoreCase(text[index..], value))
                return index;
            pos = index + 1;
        }
        return -1;
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> value)
    {
        if (span.Length < value.Length)
            return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (ToLowerAscii(span[index]) != ToLowerAscii(value[index]))
                return false;
        }
        return true;
    }

    private static byte ToLowerAscii(byte value)
        => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ('a' - 'A')) : value;

    private static bool IsLatin1Whitespace(byte value)
        => value is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0x85 or 0xA0;

    [GeneratedRegex(@"(?i)(android\d+(?:\.\d+)?[-_]\d+\.\d+(?:[-_][0-9A-Za-z.]+)?)")]
    private static partial Regex KmiRegex();
    [GeneratedRegex(@"(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionRegex();
}