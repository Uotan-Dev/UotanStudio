using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.SharedKernel;

public static class MagicRuleParser
{
    /// <summary>
    /// Parses text magic rules and returns a constructed MagicRuleSet.
    /// <para>解析文本 magic 规则，返回构造好的 MagicRuleSet。</para>
    /// </summary>
    public static MagicRuleSet Parse(TextReader reader, string source)
    {
        var ruleSet = new MagicRuleSet(source);

        var stack = new Stack<(int level, MagicRule rule)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {

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
    /// Parses rules from a string.
    /// <para>从字符串解析规则。</para>
    /// </summary>
    public static MagicRuleSet Parse(string text, string source)
    {
        using var sr = new StringReader(text);
        return Parse(sr, source);
    }

    /// <summary>
    /// Parses rules from a file path.
    /// <para>从文件解析规则。</para>
    /// </summary>
    public static MagicRuleSet ParseFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(sr, "external:" + path);
    }

    /// <summary>
    /// Parses a single rule line.
    /// <para>解析一行规则。</para>
    /// </summary>
    private static MagicRule? ParseLine(string line, string source)
    {


        int level = 0;
        int i = 0;


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


        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }


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


        if (tokens.Count < 3)
        {

            return ParseDirective(tokens, level);
        }

        return ParseNormalRule(tokens, level);
    }

    /// <summary>
    /// Parses directive lines (use/default/skip/name).
    /// <para>解析特殊指令行（use/default/skip/name 等）。</para>
    /// </summary>
    private static MagicRule? ParseDirective(List<string> tokens, int level)
    {
        string directive = tokens[0].ToLowerInvariant();


        if (directive == "default")
        {
            string descriptionTemplate = string.Empty;
            if (tokens.Count > 1)
            {
                var sb = new StringBuilder();
                for (int i = 1; i < tokens.Count; i++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(tokens[i]);
                }
                descriptionTemplate = sb.ToString();
            }
            return new MagicRule(
                0, offsetFromEnd: false, isContinuation: false,
                MagicValueType.None, integerWidth: 0, bigEndian: false, isUnsigned: false, isDate: false,
                MagicCompareOp.Any, Array.Empty<byte>(),
                descriptionTemplate, level,
                isDefault: true, noSpace: false);
        }


        if (directive is "use" or "skip" or "name" or "strength" or "clear"
            or "der" or "summary" or "ext" or "mime" or "apple" or "size")
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Parses a magic rule line containing an offset, type, value, and description.
    /// <para>解析包含偏移、类型、值和描述的 magic 规则行。</para>
    /// </summary>
    private static MagicRule? ParseNormalRule(List<string> tokens, int level)
    {

        int offset = 0;
        bool offsetFromEnd = false;
        bool isContinuation = false;
        bool isIndirect = false;
        int indirectBaseWidth = 0;
        bool indirectBigEndian = false;
        int indirectAddend = 0;
        long indirectMask = 0;
        bool indirectHasMask = false;
        bool indirectInverse = false;

        string offsetTok = tokens[0];
        if (offsetTok.StartsWith("&", StringComparison.Ordinal))
        {

            isContinuation = true;
            offsetTok = offsetTok.Substring(1);
            offset = ParseInt(offsetTok);
        }
        else if (offsetTok.StartsWith("(", StringComparison.Ordinal))
        {


            string inner = offsetTok.Trim('(', ')');
            int dot = inner.IndexOf('.');
            if (dot < 0)
            {
                return null;
            }
            string offsetPart = inner.Substring(0, dot);
            string rest = inner.Substring(dot + 1);


            bool tilde = rest.EndsWith("~", StringComparison.Ordinal);
            if (tilde)
            {
                indirectInverse = true;
                rest = rest[..^1];
            }


            int plus = rest.IndexOf('+');
            string addendPart = "0";
            if (plus >= 0)
            {
                addendPart = rest.Substring(plus + 1);
                rest = rest.Substring(0, plus);
            }


            int amp = rest.IndexOf('&');
            if (amp >= 0)
            {
                indirectMask = ParseLong(rest.Substring(amp + 1));
                indirectHasMask = true;
                rest = rest.Substring(0, amp);
            }

            (indirectBaseWidth, indirectBigEndian, _) = ParseIndirectType(rest);
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


        string typeTok = tokens[1];
        string baseType = typeTok;
        string modifiers = string.Empty;
        int slashIdx = typeTok.IndexOf('/');
        if (slashIdx >= 0)
        {
            baseType = typeTok.Substring(0, slashIdx);
            modifiers = typeTok.Substring(slashIdx + 1);
        }


        long mask = 0;
        bool hasMask = false;
        MagicMaskOp maskOp = MagicMaskOp.None;
        bool maskInverse = false;

        if (!IsStringFamily(baseType))
        {
            int maskOpIdx = IndexOfMaskOp(baseType);
            if (maskOpIdx >= 0)
            {
                string maskStr = baseType.Substring(maskOpIdx + 1);
                char op = baseType[maskOpIdx];
                maskOp = op switch
                {
                    '&' => MagicMaskOp.And,
                    '|' => MagicMaskOp.Or,
                    '^' => MagicMaskOp.Xor,
                    '+' => MagicMaskOp.Add,
                    '-' => MagicMaskOp.Subtract,
                    '*' => MagicMaskOp.Multiply,
                    '%' => MagicMaskOp.Modulo,
                    _ => MagicMaskOp.None
                };

                if (maskStr.StartsWith("~", StringComparison.Ordinal))
                {
                    maskInverse = true;
                    maskStr = maskStr.Substring(1);
                }
                mask = ParseLong(maskStr);
                hasMask = true;
                baseType = baseType.Substring(0, maskOpIdx);
            }
        }

        MagicValueType valueType = MagicValueType.None;
        int integerWidth = 0;
        bool bigEndian = false;
        bool isUnsigned = false;
        bool isDate = false;
        MagicStringFlags stringFlags = MagicStringFlags.None;
        int searchRange = 0;
        int pstringLengthBytes = 0;
        bool pstringBigEndian = false;


        if (typeTok == "name")
        {
            string name = tokens.Count > 2 ? tokens[2] : string.Empty;
            return new MagicRule(
                offset, offsetFromEnd, isContinuation,
                MagicValueType.None, integerWidth: 0, bigEndian: false, isUnsigned: false, isDate: false,
                MagicCompareOp.Equal, Array.Empty<byte>(),
                string.Empty, level, isDefault: false, noSpace: false,
                name: name);
        }
        if (typeTok == "use")
        {
            string useName = tokens.Count > 2 ? tokens[2] : string.Empty;
            return new MagicRule(
                offset, offsetFromEnd, isContinuation,
                MagicValueType.None, integerWidth: 0, bigEndian: false, isUnsigned: false, isDate: false,
                MagicCompareOp.Any, Array.Empty<byte>(),
                string.Empty, level, isDefault: false, noSpace: false,
                useName: useName);
        }
        if (typeTok == "default")
        {

            string desc = tokens.Count > 3 ? ExtractDescription(tokens) : string.Empty;
            return new MagicRule(
                offset, offsetFromEnd, isContinuation,
                MagicValueType.None, integerWidth: 0, bigEndian: false, isUnsigned: false, isDate: false,
                MagicCompareOp.Any, Array.Empty<byte>(),
                desc, level, isDefault: true, noSpace: false);
        }


        if (baseType is "skip" or "strength" or "clear"
            or "der" or "summary" or "ext" or "mime" or "apple" or "size")
        {
            return null;
        }

        if (baseType == "string")
        {
            valueType = MagicValueType.String;
            (stringFlags, searchRange) = ParseStringModifiers(modifiers);
        }
        else if (baseType == "search")
        {
            valueType = MagicValueType.Search;
            (stringFlags, searchRange) = ParseStringModifiers(modifiers);
        }
        else if (baseType == "pstring")
        {
            valueType = MagicValueType.PString;
            (stringFlags, searchRange, pstringLengthBytes, pstringBigEndian) = ParsePStringModifiers(modifiers);
        }
        else if (baseType == "regex")
        {
            valueType = MagicValueType.Regex;

            if (modifiers.Contains('c') || modifiers.Contains('C'))
            {
                stringFlags |= MagicStringFlags.IgnoreCase;
            }
        }
        else if (IsIntegerType(baseType))
        {
            (integerWidth, bigEndian, isUnsigned, isDate, valueType) = ParseIntegerType(baseType);
        }
        else
        {

            return null;
        }


        MagicCompareOp compareOp = MagicCompareOp.Equal;
        string valueTok = tokens[2];


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

                compareOp = MagicCompareOp.BitAnd;
                valueTok = valueTok.Substring(1);
            }
            else if (first == '^' && valueType == MagicValueType.Integer)
            {

                compareOp = MagicCompareOp.BitXor;
                valueTok = valueTok.Substring(1);
            }
        }


        if (valueTok == "x")
        {
            compareOp = MagicCompareOp.Any;
        }


        byte[] pattern;
        string? regexPattern = null;
        if (valueType is MagicValueType.String or MagicValueType.Search or MagicValueType.PString)
        {
            pattern = ParseStringPattern(valueTok);
        }
        else if (valueType == MagicValueType.Integer)
        {

            if (compareOp == MagicCompareOp.Any)
            {
                pattern = Array.Empty<byte>();
            }
            else
            {
                long val = ParseLong(valueTok);


                pattern = IntegerToBytes(val, integerWidth, bigEndian: true);
            }
        }
        else
        {

            pattern = Encoding.UTF8.GetBytes(valueTok);
            regexPattern = valueTok;
        }


        string descriptionTemplate = string.Empty;
        bool noSpace = false;
        if (tokens.Count > 3)
        {
            descriptionTemplate = ExtractDescription(tokens);

            if (descriptionTemplate.Length >= 2 && descriptionTemplate[0] == '\\' && descriptionTemplate[1] == 'b')
            {
                noSpace = true;
                descriptionTemplate = descriptionTemplate.Substring(2);
            }
        }

        return new MagicRule(
            offset, offsetFromEnd, isContinuation,
            valueType, integerWidth, bigEndian, isUnsigned, isDate,
            compareOp, pattern,
            descriptionTemplate, level, isDefault: false, noSpace,
            isIndirect: isIndirect, indirectBaseWidth: indirectBaseWidth,
            indirectBigEndian: indirectBigEndian, indirectAddend: indirectAddend,
            indirectMask: indirectMask, indirectHasMask: indirectHasMask,
            indirectInverse: indirectInverse,
            mask: mask, hasMask: hasMask, maskOp: maskOp, maskInverse: maskInverse,
            stringFlags: stringFlags, searchRange: searchRange,
            pstringLengthBytes: pstringLengthBytes, pstringBigEndian: pstringBigEndian,
            regexPattern: regexPattern);
    }

    /// <summary>
    /// Parses string-type modifiers (string/c, search/N): letters are flags, digits are the range.
    /// <para>解析字符串类型修饰符（string/c、search/N 等）：字母为标志，数字为搜索范围。</para>
    /// </summary>
    private static (MagicStringFlags flags, int range) ParseStringModifiers(string modifiers)
    {
        MagicStringFlags flags = MagicStringFlags.None;
        int range = 0;
        var digits = new StringBuilder();
        foreach (char c in modifiers)
        {
            switch (c)
            {
                case 'c':
                case 'C':
                    flags |= MagicStringFlags.IgnoreCase;
                    break;
                case 'W':
                    flags |= MagicStringFlags.CompactWhitespace;
                    break;
                case 'w':
                    flags |= MagicStringFlags.OptionalWhitespace;
                    break;
                case 'T':
                    flags |= MagicStringFlags.Trim;
                    break;
                case 'f':
                    flags |= MagicStringFlags.FullWord;
                    break;
                case 't':
                case 'b':
                case 's':

                    break;
                default:
                    if (char.IsAsciiDigit(c))
                    {
                        digits.Append(c);
                    }
                    break;
            }
        }
        if (digits.Length > 0)
        {
            range = ParseInt(digits.ToString());
        }
        return (flags, range);
    }

    /// <summary>
    /// Parses pstring modifiers (B/H/h/L/l/J length prefixes).
    /// <para>解析 pstring 修饰符（B/H/h/L/l/J 长度前缀）。</para>
    /// </summary>
    private static (MagicStringFlags flags, int range, int lengthBytes, bool bigEndian) ParsePStringModifiers(string modifiers)
    {
        MagicStringFlags flags = MagicStringFlags.None;
        int range = 0;
        int lengthBytes = 1;
        bool bigEndian = false;
        foreach (char c in modifiers)
        {
            switch (c)
            {
                case 'B':
                    lengthBytes = 1;
                    bigEndian = false;
                    break;
                case 'H':
                    lengthBytes = 2;
                    bigEndian = true;
                    break;
                case 'h':
                    lengthBytes = 2;
                    bigEndian = false;
                    break;
                case 'L':
                    lengthBytes = 4;
                    bigEndian = true;
                    break;
                case 'l':
                    lengthBytes = 4;
                    bigEndian = false;
                    break;
                case 'J':

                    break;
                case 'c':
                case 'C':
                    flags |= MagicStringFlags.IgnoreCase;
                    break;
                case 'W':
                    flags |= MagicStringFlags.CompactWhitespace;
                    break;
                case 'w':
                    flags |= MagicStringFlags.OptionalWhitespace;
                    break;
                case 'T':
                    flags |= MagicStringFlags.Trim;
                    break;
                case 'f':
                    flags |= MagicStringFlags.FullWord;
                    break;
                default:
                    if (char.IsAsciiDigit(c))
                    {
                        range = range * 10 + (c - '0');
                    }
                    break;
            }
        }
        return (flags, range, lengthBytes, bigEndian);
    }

    /// <summary>
    /// Tokenization: splits on whitespace, keeps escape sequences intact.
    /// <para>Token 化：按空格/Tab 分隔，转义字符（\xNN 等）保持完整。</para>
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
    /// Parses a string pattern with \xNN/\0/\a/\b/\f/\n/\r/\t/\v/\\/whitespace escapes.
    /// <para>解析字符串 pattern，支持 \xNN、\0、\a、\b、\f、\n、\r、\t、\v、\\、\ 空白转义。</para>
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
    /// Parses the indirect-offset type letter (upstream in_type single-letter codes): l=lelong, L=belong, h/s=leshort, H/S=beshort, c/b/C/B=byte, m=melong, q=lequad, Q=bequad; full type names (belong/lelong ...) are also accepted.
    /// <para>解析间接偏移的类型字母（上游 in_type 单字母编码）：l=lelong、L=belong、 h/s=leshort、H/S=beshort、c/b/C/B=byte、m=melong、q=lequad、Q=bequad； 同时兼容完整类型名（belong/lelong 等）。</para>
    /// </summary>
    private static (int width, bool bigEndian, MagicValueType valueType) ParseIndirectType(string type)
    {
        return type switch
        {
            "b" or "B" or "c" or "C" => (1, true, MagicValueType.Integer),
            "h" or "s" => (2, false, MagicValueType.Integer),
            "H" or "S" => (2, true, MagicValueType.Integer),
            "l" => (4, false, MagicValueType.Integer),
            "L" => (4, true, MagicValueType.Integer),
            "m" => (4, false, MagicValueType.Integer),
            "q" => (8, false, MagicValueType.Integer),
            "Q" => (8, true, MagicValueType.Integer),
            _ => ParseIntegerType(type) switch
            {
                var (w, be, _, _, vt) => (w, be, vt)
            }
        };
    }

    /// <summary>
    /// Determines whether the type is a string-family type (string/search/pstring/regex, whose '/' introduces modifiers rather than a mask).
    /// <para>判断是否为字符串族类型（string/search/pstring/regex，其 '/' 后为修饰符而非掩码）。</para>
    /// </summary>
    private static bool IsStringFamily(string type) =>
        type == "string" || type == "search" || type == "pstring" || type == "regex";

    /// <summary>
    /// Determines whether the type is an integer type (including u/be/le prefix variants).
    /// <para>判断是否为整数类型（含 u 无符号前缀与 be/le 前缀变体）。</para>
    /// </summary>
    private static bool IsIntegerType(string type)
    {
        if (type.StartsWith("u", StringComparison.Ordinal) && type.Length > 1)
        {
            type = type.Substring(1);
        }
        return type == "byte" || type == "short" || type == "long" || type == "quad" ||
               type == "date" || type == "ledate" || type == "bedate" || type == "ldate" ||
               type == "beldate" || type == "leldate" ||
               type.StartsWith("be", StringComparison.Ordinal) ||
               type.StartsWith("le", StringComparison.Ordinal) ||
               type.StartsWith("id", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses an integer type: returns byte width, endianness, unsigned flag, date flag and value type.
    /// <para>解析整数类型：返回字节宽度、字节序、无符号标志、日期标志与值类型。</para>
    /// </summary>
    private static (int width, bool bigEndian, bool isUnsigned, bool isDate, MagicValueType valueType) ParseIntegerType(string type)
    {
        bool bigEndian = true;
        bool isUnsigned = false;
        bool isDate = false;
        string baseType = type;

        if (type.StartsWith("u", StringComparison.Ordinal))
        {
            isUnsigned = true;
            baseType = type.Substring(1);
        }
        if (baseType.StartsWith("be", StringComparison.Ordinal))
        {
            bigEndian = true;
            baseType = baseType.Substring(2);
        }
        else if (baseType.StartsWith("le", StringComparison.Ordinal))
        {
            bigEndian = false;
            baseType = baseType.Substring(2);
        }
        else if (baseType.StartsWith("id", StringComparison.Ordinal))
        {

            bigEndian = true;
            baseType = baseType.Substring(2);
        }

        int width = baseType switch
        {
            "byte" => 1,
            "short" => 2,
            "long" => 4,
            "quad" => 8,
            "date" or "ldate" => 4,
            _ => 4
        };
        if (baseType is "date" or "ldate" or "beldate" or "leldate" || type is "date" or "ledate" or "bedate" or "ldate" or "beldate" or "leldate")
        {
            isDate = true;
        }

        return (width, bigEndian, isUnsigned, isDate, MagicValueType.Integer);
    }

    /// <summary>
    /// Parses an integer: supports 0xNN, decimal, negative sign, octal (0NN).
    /// <para>解析整数：支持 0xNN、十进制、负号、八进制（0NN）。</para>
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
    /// Parses a long integer: supports 0xNN, decimal, negative sign, octal.
    /// <para>解析长整数：支持 0xNN、十进制、负号、八进制。</para>
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
    /// Converts an integer value to a byte array with the specified width and endianness.
    /// <para>将整数值转换为指定宽度和字节序的字节数组。</para>
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
    /// Extracts the description template from the token list (token[3..] joined by spaces).
    /// <para>从 token 列表提取描述模板（token[3..] 合并，空格分隔）。</para>
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

    /// <summary>
    /// Finds the first mask-operator character (&amp;|^+-*/%; type names contain none of these).
    /// <para>查找掩码运算符位置（&amp;|^+-*/% 中的首个；类型名中不含这些字符）。</para>
    /// </summary>
    private static int IndexOfMaskOp(string type)
    {
        for (int i = 0; i < type.Length; i++)
        {
            char c = type[i];
            if (c is '&' or '|' or '^' or '+' or '-' or '*' or '/' or '%')
            {
                return i;
            }
        }
        return -1;
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
