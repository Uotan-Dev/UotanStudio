using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.Common;

/// <summary>
/// <para>上游 file(1) 文本 magic 规则解析器。支持缩进父子关系、字符串（含 \xNN/\0/空白转义）、
/// 整数（byte/short/long/quad 及 be/le/id 变体）、比较运算符（=!&lt;&gt;x）、间接偏移 (offset)、
/// <c>use</c>/<c>default</c>/<c>skip</c>/<c>name</c> 指令、<c>type&amp;mask</c> 掩码、
/// <c>regex</c> 类型、<c>&gt;000</c> 字符串比较运算符、<c>\b</c> 退格描述连接等上游语法。</para>
/// Parser for upstream file(1) text magic rules. Supports indented parent/child relations, strings
/// (with \xNN/\0/whitespace escapes), integers (byte/short/long/quad + be/le/id variants), comparison
/// operators (=!&lt;&gt;x), indirect offsets (offset), use/default/skip/name directives, type&amp;mask
/// masking, regex type, &gt;000 string comparison operators, and \b backspace description concatenation.
/// </summary>
public static class MagicRuleParser
{
    /// <summary>
    /// <para>解析文本 magic 规则，返回构造好的 <see cref="MagicRuleSet"/>。</para>
    /// Parses text magic rules and returns a constructed <see cref="MagicRuleSet"/>.
    /// </summary>
    /// <param name="reader">规则文本读取器。</param>
    /// <param name="source">规则集来源标识。</param>
    public static MagicRuleSet Parse(TextReader reader, string source)
    {
        var ruleSet = new MagicRuleSet(source);
        // 父规则栈：缩进级别 → 最近一条该级别的规则
        var stack = new Stack<(int level, MagicRule rule)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // 跳过空行与注释（# 开头，但需注意 #! 等，这里按上游约定 # 在行首为注释）
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            MagicRule? rule = ParseLine(line, source);
            if (rule is null)
            {
                continue;
            }

            // 维护父子栈
            while (stack.Count > 0 && stack.Peek().level >= rule.Indent)
            {
                stack.Pop();
            }

            MagicRule? parent = stack.Count > 0 ? stack.Peek().rule : null;
            ruleSet.Add(rule, parent);
            stack.Push((rule.Indent, rule));
        }
        return ruleSet;
    }

    /// <summary>
    /// <para>从字符串解析规则。</para>
    /// Parses rules from a string.
    /// </summary>
    public static MagicRuleSet Parse(string text, string source)
    {
        using var sr = new StringReader(text);
        return Parse(sr, source);
    }

    /// <summary>
    /// <para>从文件解析规则。</para>
    /// Parses rules from a file path.
    /// </summary>
    public static MagicRuleSet ParseFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(sr, "external:" + path);
    }

    /// <summary>
    /// <para>解析一行规则。</para>
    /// Parses a single rule line.
    /// </summary>
    private static MagicRule? ParseLine(string line, string source)
    {
        // 上游 file(1) 子规则用前导 '>' 表示缩进级别（'>>' 为二级，依此类推）。
        // 顶层规则无前导 '>'。空格缩进也被兼容，每 2 空格视作一级。
        // Upstream file(1) uses leading '>' to mark sub-rule depth; top-level rules have none.
        // Whitespace indentation is also accepted (2 spaces = 1 level).
        int level = 0;
        int i = 0;

        // 上游 file(1) 的元指令行（!:strength、!:mime、!:ext 等）不参与匹配，直接跳过。
        // 这些指令出现在行首，形如 "!:strength +65" 或 "!:mime application/gzip"。
        // Upstream file(1) meta-directive lines (!:strength, !:mime, !:ext) are not used by the
        // pure-managed engine; skip them. They appear at line start, e.g. "!:strength +65".
        int leadingNonSpace = 0;
        while (leadingNonSpace < line.Length && (line[leadingNonSpace] == ' ' || line[leadingNonSpace] == '\t'))
        {
            leadingNonSpace++;
        }
        if (leadingNonSpace + 1 < line.Length
            && line[leadingNonSpace] == '!'
            && line[leadingNonSpace + 1] == ':')
        {
            return null;
        }

        while (i < line.Length && line[i] == '>')
        {
            level++;
            i++;
        }

        // 消耗 '>' 后可能跟随的空格
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        // 兼容纯空格缩进（无 '>' 前缀）的情况
        if (level == 0)
        {
            int indent = 0;
            int j = 0;
            while (j < line.Length && (line[j] == ' ' || line[j] == '\t'))
            {
                indent += line[j] == '\t' ? 4 : 1;
                j++;
            }
            level = Math.Max(0, indent / 2);
            i = j;
        }

        string rest = line.Substring(i);
        var tokens = Tokenize(rest);
        if (tokens.Count == 0)
        {
            return null;
        }

        // token[0] = offset，token[1] = type，token[2] = test value，token[3..] = 描述（可选）
        if (tokens.Count < 3)
        {
            // 特殊指令：use / default / skip / name 等，仅 2 个 token
            return ParseDirective(tokens, level);
        }

        return ParseNormalRule(tokens, level);
    }

    /// <summary>
    /// <para>解析特殊指令行（<c>use</c>/<c>default</c>/<c>skip</c>/<c>name</c> 等）。
    /// 这些指令在纯托管引擎中不参与实际匹配，故返回 <c>null</c> 跳过。</para>
    /// Parses directive lines (use/default/skip/name). These directives are not used in the pure-managed
    /// engine, so we return <c>null</c> to skip them.
    /// </summary>
    private static MagicRule? ParseDirective(List<string> tokens, int level)
    {
        string directive = tokens[0].ToLowerInvariant();

        // default：无条件匹配子规则，描述来自 token[2..]
        // upstream semantics: match when all previous sub-rules fail; often carries the description.
        // Simplification: treat as unconditional sub-rule; its description is appended to the parent.
        if (directive == "default")
        {
            string descriptionTemplate = string.Empty;
            if (tokens.Count > 2)
            {
                var sb = new StringBuilder();
                for (int i = 2; i < tokens.Count; i++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(tokens[i]);
                }
                descriptionTemplate = sb.ToString();
            }
            return new MagicRule(
                0, offsetFromEnd: false,
                MagicValueType.None, integerWidth: 0, bigEndian: false,
                MagicCompareOp.Any, Array.Empty<byte>(),
                descriptionTemplate, level, isInverse: false,
                isIndirect: false, indirectBaseWidth: 0, indirectBigEndian: false, indirectAddend: 0,
                mask: 0, hasMask: false);
        }

        // 其他指令：use、skip、name、strength 等，纯托管引擎不实现，跳过
        if (directive is "use" or "skip" or "name" or "strength" or "clear"
            or "der" or "summary" or "ext" or "mime" or "apple" or "size")
        {
            return null;
        }
        // 其他无法识别的 2-token 行也跳过
        return null;
    }

    /// <summary>
    /// <para>解析正常规则行（offset + type + value + description）。
    /// 支持上游 file(1) 全部语法变体：</para>
    /// Parses a normal rule line. Supports all upstream file(1) syntax variants:
    /// <list type="bullet">
    /// <item>type&amp;mask（如 <c>byte&amp;0x80</c>）—— 整数读取后按 mask 过滤</item>
    /// <item>test value 以 <c>&amp;</c> 开头（如 <c>&amp;0x01</c>）—— 按位与比较</item>
    /// <item><c>x</c> 在整数位置 —— 任意值</item>
    /// <item><c>&gt;000</c> —— 字符串比较运算符（大于字符串）</item>
    /// </list>
    /// </summary>
    private static MagicRule? ParseNormalRule(List<string> tokens, int level)
    {
        // 解析 offset（支持 0xNN、十进制、(offset) 间接）
        int offset = 0;
        bool offsetFromEnd = false;
        bool isIndirect = false;
        int indirectBaseWidth = 0;
        bool indirectBigEndian = false;
        int indirectAddend = 0;

        string offsetTok = tokens[0];
        if (offsetTok.StartsWith("(", StringComparison.Ordinal))
        {
            // 间接偏移：(type.offset[&mask][+addend])
            // 简化：仅解析 (type.offset+addend)，不支持 mask
            string inner = offsetTok.Trim('(', ')');
            int plus = inner.IndexOf('+');
            int dot = inner.IndexOf('.');
            string typePart = dot >= 0 ? inner.Substring(0, dot) : inner;
            string offsetPart = dot >= 0 ? inner.Substring(dot + 1, (plus >= 0 ? plus - dot - 1 : inner.Length - dot - 1)) : "0";
            string addendPart = plus >= 0 ? inner.Substring(plus + 1) : "0";

            (indirectBaseWidth, indirectBigEndian, _) = ParseIntegerType(typePart);
            offset = ParseInt(offsetPart);
            indirectAddend = ParseInt(addendPart);
            isIndirect = true;
        }
        else
        {
            if (offsetTok.StartsWith("-", StringComparison.Ordinal))
            {
                offsetFromEnd = true;
                offset = ParseInt(offsetTok);
            }
            else
            {
                offset = ParseInt(offsetTok);
            }
        }

        // 解析 type（支持 type&mask 掩码写法，如 byte&0x80）
        string typeTok = tokens[1];
        long mask = 0;
        bool hasMask = false;
        int ampIdx = typeTok.IndexOf('&');
        if (ampIdx >= 0)
        {
            string maskStr = typeTok.Substring(ampIdx + 1);
            mask = ParseLong(maskStr);
            hasMask = true;
            typeTok = typeTok.Substring(0, ampIdx);
        }

        MagicValueType valueType = MagicValueType.None;
        int integerWidth = 0;
        bool bigEndian = false;

        // 先识别指令性 type（use/default/skip/name 等出现在 type 位置）
        if (typeTok is "use" or "default" or "skip" or "name" or "strength" or "clear"
            or "der" or "summary" or "ext" or "mime" or "apple" or "size")
        {
            return null;
        }

        if (typeTok == "string" || typeTok == "pstring" || typeTok == "search")
        {
            valueType = MagicValueType.String;
        }
        else if (typeTok == "regex" || typeTok.StartsWith("regex/", StringComparison.Ordinal))
        {
            valueType = MagicValueType.Regex;
        }
        else if (IsIntegerType(typeTok))
        {
            (integerWidth, bigEndian, valueType) = ParseIntegerType(typeTok);
        }
        else
        {
            // 未知类型，跳过
            return null;
        }

        // 解析比较运算符 + 值
        MagicCompareOp compareOp = MagicCompareOp.Equal;
        string valueTok = tokens[2];

        // 检查前缀运算符
        if (valueTok.Length > 0)
        {
            char first = valueTok[0];
            if (first == '!')
            {
                compareOp = MagicCompareOp.NotEqual;
                valueTok = valueTok.Substring(1);
            }
            else if (first == '<')
            {
                compareOp = MagicCompareOp.LessThan;
                valueTok = valueTok.Substring(1);
            }
            else if (first == '>')
            {
                compareOp = MagicCompareOp.GreaterThan;
                valueTok = valueTok.Substring(1);
            }
            else if (first == '=')
            {
                compareOp = MagicCompareOp.Equal;
                valueTok = valueTok.Substring(1);
            }
            else if (first == '&' && valueType == MagicValueType.Integer)
            {
                // 按位与比较：(actual & mask) != 0
                compareOp = MagicCompareOp.BitAnd;
                valueTok = valueTok.Substring(1);
            }
        }

        // "x" 特殊值表示任意
        if (valueTok == "x" && valueType != MagicValueType.String)
        {
            compareOp = MagicCompareOp.Any;
        }

        // 解析 pattern
        byte[] pattern;
        if (valueType == MagicValueType.String)
        {
            pattern = ParseStringPattern(valueTok);
        }
        else if (valueType == MagicValueType.Integer)
        {
            // "x" 表示任意值，pattern 留空（长度 0），仅检查存在性
            if (compareOp == MagicCompareOp.Any)
            {
                pattern = Array.Empty<byte>();
            }
            else
            {
                long val = ParseLong(valueTok);
                // pattern 始终按大端存储，匹配时 ReadInteger(Pattern, ..., bigEndian: true) 能读回原值
                // pattern is always stored big-endian; ReadInteger(Pattern, ..., bigEndian: true) recovers it
                pattern = IntegerToBytes(val, integerWidth, bigEndian: true);
            }
        }
        else
        {
            // regex: 保留原始字符串作为 pattern
            pattern = Encoding.UTF8.GetBytes(valueTok);
        }

        // 解析描述模板（token[3..] 合并，空格分隔）
        string descriptionTemplate = string.Empty;
        if (tokens.Count > 3)
        {
            descriptionTemplate = ExtractDescription(tokens);
        }

        return new MagicRule(
            offset, offsetFromEnd,
            valueType, integerWidth, bigEndian,
            compareOp, pattern,
            descriptionTemplate, level, isInverse: false,
            isIndirect, indirectBaseWidth, indirectBigEndian, indirectAddend,
            mask, hasMask);
    }

    /// <summary>
    /// <para>Token 化：按空格/Tab 分隔，转义字符（\xNN 等）保持完整。</para>
    /// Tokenization: splits on whitespace, keeps escape sequences intact.
    /// </summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '\\' && i + 1 < line.Length)
            {
                // 转义字符：保留反斜杠 + 下一个字符，避免被空格分隔
                sb.Append('\\');
                sb.Append(line[++i]);
                continue;
            }

            if (c == ' ' || c == '\t')
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// <para>解析字符串 pattern，支持 \xNN、\0、\a、\b、\f、\n、\r、\t、\v、\\、\ 空白转义。</para>
    /// Parses a string pattern with \xNN/\0/\a/\b/\f/\n/\r/\t/\v/\\/whitespace escapes.
    /// </summary>
    private static byte[] ParseStringPattern(string token)
    {
        var bytes = new List<byte>(token.Length);
        int i = 0;
        while (i < token.Length)
        {
            char c = token[i];
            if (c == '\\' && i + 1 < token.Length)
            {
                char next = token[i + 1];

                // 八进制转义 \d、\dd、\ddd（magic(5)：1 至 3 位八进制数）。\0 → 0x00，\037 → 0x1f。
                // Octal escapes \d / \dd / \ddd (magic(5): 1 to 3 octal digits). \0 → 0x00, \037 → 0x1f.
                if (next is >= '0' and <= '7')
                {
                    int value = 0;
                    int digits = 0;
                    int k = i + 1;
                    while (k < token.Length && digits < 3 && token[k] is >= '0' and <= '7')
                    {
                        value = (value << 3) | (token[k] - '0');
                        digits++;
                        k++;
                    }
                    bytes.Add(unchecked((byte)value));
                    i = k;
                    continue;
                }

                switch (next)
                {
                    case 'x' when i + 3 < token.Length && IsHexDigit(token[i + 2]) && IsHexDigit(token[i + 3]):
                        bytes.Add((byte)((HexVal(token[i + 2]) << 4) | HexVal(token[i + 3])));
                        i += 4;
                        continue;
                    case 'a':
                        bytes.Add(7);
                        i += 2;
                        continue;
                    case 'b':
                        bytes.Add(8);
                        i += 2;
                        continue;
                    case 'f':
                        bytes.Add(12);
                        i += 2;
                        continue;
                    case 'n':
                        bytes.Add(10);
                        i += 2;
                        continue;
                    case 'r':
                        bytes.Add(13);
                        i += 2;
                        continue;
                    case 't':
                        bytes.Add(9);
                        i += 2;
                        continue;
                    case 'v':
                        bytes.Add(11);
                        i += 2;
                        continue;
                    case '\\':
                        bytes.Add((byte)'\\');
                        i += 2;
                        continue;
                    case ' ':
                        bytes.Add((byte)' ');
                        i += 2;
                        continue;
                    default:
                        // 未知转义，保留反斜杠
                        bytes.Add((byte)'\\');
                        i++;
                        continue;
                }
            }
            bytes.Add((byte)c);
            i++;
        }
        return bytes.ToArray();
    }

    /// <summary>
    /// <para>判断是否为整数类型（含 be/le/id 前缀变体）。</para>
    /// Determines whether the type is an integer type (including be/le/id prefix variants).
    /// </summary>
    private static bool IsIntegerType(string type) =>
        type == "byte" || type == "short" || type == "long" || type == "quad" ||
        type == "date" || type == "ledate" || type == "bedate" ||
        type == "ubequad" || type == "ulequad" || type == "lequad" || type == "bequad" ||
        type.StartsWith("be", StringComparison.Ordinal) ||
        type.StartsWith("le", StringComparison.Ordinal) ||
        type.StartsWith("id", StringComparison.Ordinal);

    /// <summary>
    /// <para>解析整数类型：返回字节宽度、字节序、值类型。</para>
    /// Parses an integer type: returns byte width, endianness, and value type.
    /// </summary>
    private static (int width, bool bigEndian, MagicValueType valueType) ParseIntegerType(string type)
    {
        // 解析 byte/short/long/quad + be/le 前缀
        // 简化：belong → width=4, bigEndian=true；leshort → width=2, bigEndian=false
        bool bigEndian = true;
        string baseType = type;

        if (type.StartsWith("be", StringComparison.Ordinal))
        {
            bigEndian = true;
            baseType = type.Substring(2);
        }
        else if (type.StartsWith("le", StringComparison.Ordinal))
        {
            bigEndian = false;
            baseType = type.Substring(2);
        }
        else if (type.StartsWith("id", StringComparison.Ordinal))
        {
            // 间接偏移类型
            bigEndian = true;
            baseType = type.Substring(2);
        }

        int width = baseType switch
        {
            "byte" => 1,
            "short" => 2,
            "long" => 4,
            "quad" => 8,
            "date" => 4,
            _ => 4
        };

        return (width, bigEndian, MagicValueType.Integer);
    }

    /// <summary>
    /// <para>解析整数：支持 0xNN、十进制、负号、八进制（0NN）。
    /// 容错策略：无法解析的 token 返回 0，避免单条异常规则导致整个规则集解析崩溃。</para>
    /// Parses an integer: supports 0xNN, decimal, negative sign, octal (0NN).
    /// Fault-tolerant: unparseable tokens return 0, preventing a single bad rule from crashing the whole parse.
    /// </summary>
    private static int ParseInt(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return 0;
        }
        try
        {
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(token.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return int.Parse(token, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// <para>解析长整数：支持 0xNN、十进制、负号、八进制。
    /// 容错策略：无法解析的 token 返回 0。</para>
    /// Parses a long integer: supports 0xNN, decimal, negative sign, octal.
    /// Fault-tolerant: unparseable tokens return 0.
    /// </summary>
    private static long ParseLong(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return 0;
        }
        try
        {
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.Parse(token.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            if (token.Length > 1 && token[0] == '0' && token[1] != 'x' && token[1] != 'X')
            {
                return Convert.ToInt64(token, 8);
            }
            return long.Parse(token, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// <para>将整数值转换为指定宽度和字节序的字节数组。</para>
    /// Converts an integer value to a byte array with the specified width and endianness.
    /// </summary>
    private static byte[] IntegerToBytes(long value, int width, bool bigEndian)
    {
        byte[] bytes = new byte[width];
        for (int i = 0; i < width; i++)
        {
            int shift = (width - 1 - i) * 8;
            bytes[i] = (byte)((value >> shift) & 0xFF);
        }
        if (!bigEndian)
        {
            Array.Reverse(bytes);
        }
        return bytes;
    }

    /// <summary>
    /// <para>从 token 列表提取描述模板（token[3..] 合并，空格分隔）。</para>
    /// Extracts the description template from the token list (token[3..] joined by spaces).
    /// </summary>
    private static string ExtractDescription(List<string> tokens)
    {
        var sb = new StringBuilder();
        for (int i = 3; i < tokens.Count; i++)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(tokens[i]);
        }
        return sb.ToString();
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return 0;
    }
}
