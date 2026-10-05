using System.Collections.Frozen;
using System.Text;

namespace UotanStudio.Firmware.FileSystem.Metadata;

/// <summary>
/// Translates the Android file-context regex dialect (RE2 with POSIX classes and Perl shorthands) into a
/// .NET pattern whose classes are explicit ASCII ranges, keeping byte-oriented matching semantics stable.
/// <para>将 Android file_contexts 正则方言（带 POSIX 类与 Perl 简写的 RE2）翻译为 .NET 模式，其中字符类展开为显式
/// ASCII 区间，保持按字节匹配的语义稳定。</para>
/// </summary>
internal static class SelinuxPolicyRegexTranslator
{
    private static readonly FrozenDictionary<string, string> PosixClasses = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["alpha"] = "A-Za-z",
        ["digit"] = "0-9",
        ["alnum"] = "A-Za-z0-9",
        ["upper"] = "A-Z",
        ["lower"] = "a-z",
        ["word"] = "0-9A-Za-z_",
        ["xdigit"] = "0-9A-Fa-f",
        ["space"] = " \\t-\\r",
        ["blank"] = " \\t",
        ["punct"] = "!-/:-@[-`{-~",
        ["graph"] = "!-~",
        ["print"] = " -~",
        ["cntrl"] = "\\x00-\\x1F\\x7F",
        ["ascii"] = "\\x00-\\x7F",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<char, string> Shorthands = new Dictionary<char, string>
    {
        ['d'] = "0-9",
        ['D'] = "^0-9",
        ['w'] = "0-9A-Za-z_",
        ['W'] = "^0-9A-Za-z_",
        ['s'] = " \\t-\\r",
        ['S'] = "^ \\t-\\r",
    }.ToFrozenDictionary();

    /// <summary>
    /// Rewrites one file-context regex into .NET syntax, expanding POSIX classes and Perl shorthands into
    /// explicit ASCII ranges and rejecting syntax without a stable byte-level device equivalent.
    /// <para>把一个 file_contexts 正则改写为 .NET 语法：POSIX 类与 Perl 简写展开为显式 ASCII 区间，缺少稳定按字节
    /// 设备语义的语法会被拒绝。</para>
    /// </summary>
    /// <param name="pattern">Raw regex source from a file-contexts rule line.
    /// <para>来自 file_contexts 规则行的原始正则文本。</para></param>
    /// <param name="sourcePath">Extraction-relative policy file path used in failure messages.
    /// <para>失败消息中使用的、相对解包目录的策略文件路径。</para></param>
    /// <returns>Equivalent .NET regex source. <para>等价的 .NET 正则源码。</para></returns>
    /// <exception cref="FileSystemUnsupportedFeatureException">The pattern uses unsupported or malformed syntax.
    /// <para>模式使用了不支持或格式错误的语法。</para></exception>
    internal static string Translate(string pattern, string sourcePath)
    {
        StringBuilder result = new(pattern.Length);
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    throw Unsupported(sourcePath, pattern);
                }
                i = TranslateEscape(pattern, i, result, inClass: false, sourcePath);
            }
            else if (c == '(')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                {
                    if (i + 2 >= pattern.Length || pattern[i + 2] != ':')
                    {
                        throw Unsupported(sourcePath, pattern);
                    }
                    result.Append("(?:");
                    i += 3;
                }
                else
                {
                    result.Append('(');
                    i++;
                }
            }
            else if (c == '[')
            {
                i = TranslateClass(pattern, i, result, sourcePath);
            }
            else if (c == '&' && i + 1 < pattern.Length && pattern[i + 1] == '&')
            {
                throw Unsupported(sourcePath, pattern);
            }
            else
            {
                result.Append(c);
                i++;
            }
        }
        return result.ToString();
    }

    private static int TranslateClass(string pattern, int index, StringBuilder result, string sourcePath)
    {
        var i = index + 1;
        var negated = false;
        if (i < pattern.Length && pattern[i] == '^')
        {
            negated = true;
            i++;
        }
        StringBuilder body = new();
        var negationToken = false;
        var items = 0;
        while (i < pattern.Length && pattern[i] != ']')
        {
            if (negationToken)
            {
                throw Unsupported(sourcePath, pattern);
            }
            if (pattern.AsSpan(i).StartsWith("[:", StringComparison.Ordinal))
            {
                i += 2;
                var tokenNegated = i < pattern.Length && pattern[i] == '^';
                if (tokenNegated)
                {
                    i++;
                }
                var nameStart = i;
                while (i < pattern.Length && char.IsAsciiLetterLower(pattern[i]))
                {
                    i++;
                }
                if (i + 1 >= pattern.Length || pattern[i] != ':' || pattern[i + 1] != ']'
                    || !PosixClasses.TryGetValue(pattern[nameStart..i], out var fragment)
                    || tokenNegated && (negated || items > 0))
                {
                    throw Unsupported(sourcePath, pattern);
                }
                i += 2;
                body.Append(fragment);
                items++;
                negationToken = tokenNegated;
                continue;
            }
            var c = pattern[i];
            if (c == '&' && i + 1 < pattern.Length && pattern[i + 1] == '&'
                || c == '[' && body.Length > 0 && body[^1] == '-')
            {
                throw Unsupported(sourcePath, pattern);
            }
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    throw Unsupported(sourcePath, pattern);
                }
                i = TranslateEscape(pattern, i, body, inClass: true, sourcePath);
                items++;
                continue;
            }
            body.Append(c);
            i++;
            items++;
        }
        if (i >= pattern.Length || body.Length == 0)
        {
            throw Unsupported(sourcePath, pattern);
        }
        result.Append(negated || negationToken ? "[^" : "[");
        result.Append(body);
        result.Append(']');
        return i + 1;
    }

    private static int TranslateEscape(string pattern, int index, StringBuilder result, bool inClass, string sourcePath)
    {
        var kind = pattern[index + 1];
        if (Shorthands.TryGetValue(kind, out var fragment))
        {
            if (inClass && char.IsUpper(kind))
            {
                throw Unsupported(sourcePath, pattern);
            }
            if (inClass)
            {
                result.Append(fragment);
            }
            else
            {
                result.Append('[').Append(fragment).Append(']');
            }
            return index + 2;
        }
        if (char.IsAsciiLetter(kind) || char.IsAsciiDigit(kind))
        {
            throw Unsupported(sourcePath, pattern);
        }
        result.Append('\\').Append(kind);
        return index + 2;
    }

    private static FileSystemUnsupportedFeatureException Unsupported(string sourcePath, string pattern)
        => new($"Unsupported SELinux regex in {sourcePath}: {pattern}");
}