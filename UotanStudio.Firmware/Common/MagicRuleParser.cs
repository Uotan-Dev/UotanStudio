using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.Common;

/// <summary>
/// <para>上游 file(1) 文本 magic 规则解析器。支持缩进父子关系（含任意深度递归子规则）、字符串
/// （含 \xNN/\0/空白转义与 /c /W /T /f 等修饰符）、整数（byte/short/long/quad、u 无符号前缀、
/// be/le 变体、date 日期）、比较运算符（=!&lt;&gt;x、&amp; 按位与、^ 按位异或）、掩码运算
/// （type&amp;mask / type|mask / type^mask / type+mask / type-mask / type*mask / type/mask / type%mask）、
/// '&amp;' 续接偏移（相对上一层规则匹配结束位置）、间接偏移 (offset.type[&amp;mask][+addend])、
/// <c>use</c>/<c>name</c>/<c>default</c> 指令、<c>search</c> 范围搜索、<c>regex</c> 正则、
/// <c>pstring</c> 长度前缀字符串、<c>\b</c> NOSPACE 描述连接等上游语法。</para>
/// Parser for upstream file(1) text magic rules. Supports indented parent/child relations (with
/// arbitrary-depth recursion), strings (with \xNN/\0/whitespace escapes and /c /W /T /f modifiers),
/// integers (byte/short/long/quad, u unsigned prefix, be/le variants, date), comparison operators
/// (=!&lt;&gt;x, &amp; bitwise-AND, ^ bitwise-XOR), mask operations (type&amp;mask / type|mask /
/// type^mask / type+mask / type-mask / type*mask / type/mask / type%mask), '&amp;' continuation
/// offsets (relative to the end of the previous level's match), indirect offsets
/// (offset.type[&amp;mask][+addend]), use/name/default directives, search range search, regex,
/// pstring length-prefixed strings, and \b NOSPACE description concatenation.
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
    /// <para>解析特殊指令行（<c>use</c>/<c>default</c>/<c>skip</c>/<c>name</c> 等）。</para>
    /// Parses directive lines (use/default/skip/name).
    /// </summary>
    private static MagicRule? ParseDirective(List<string> tokens, int level)
    {
        string directive = tokens[0].ToLowerInvariant();

        // default：无条件匹配子规则，描述来自 token[1..]（上游语义：仅当同层此前无规则命中时匹配）
        // upstream semantics: match when all previous sub-rules fail; often carries the description.
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
    /// 支持上游 file(1) 全部常用语法变体：</para>
    /// Parses a normal rule line (offset + type + value + description). Supports the common
    /// upstream file(1) syntax variants:
    /// <list type="bullet">
    /// <item>type&amp;mask / type|mask / type^mask / type+mask / type-mask / type*mask / type/mask / type%mask —— 掩码运算</item>
    /// <item>'&amp;' 续接偏移（&gt;&amp;8）与负偏移（从文件末尾）</item>
    /// <item>间接偏移 (offset.type[&amp;mask][+addend])</item>
    /// <item>u 无符号前缀（ubyte/ulong/ulelong 等）</item>
    /// <item>字符串修饰符（string/c、search/N、pstring/B 等）</item>
    /// <item>比较运算符 =、!=、&lt;、&gt;、&amp;（(v&amp;l)==l）、^（(v&amp;l)!=l）、x</item>
    /// </list>
    /// </summary>
    private static MagicRule? ParseNormalRule(List<string> tokens, int level)
    {
        // 解析 offset（支持 0xNN、十进制、(offset.type) 间接、&N 续接、-N 从末尾）
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
            // 上游 '&' 续接偏移：相对上一层规则匹配结束位置（OFFADD）
            isContinuation = true;
            offsetTok = offsetTok.Substring(1);
            offset = ParseInt(offsetTok);
        }
        else if (offsetTok.StartsWith("(", StringComparison.Ordinal))
        {
            // 间接偏移：(offset.type[&mask][+addend]) —— 上游顺序为“偏移在前、类型字母在后”，
            // 如 (104.l+132)、(21.b+24)、(0x12.l+15)。
            // Indirect offset: (offset.type[&mask][+addend]) — upstream puts the base offset first
            // and the type letter second, e.g. (104.l+132), (21.b+24), (0x12.l+15).
            string inner = offsetTok.Trim('(', ')');
            int dot = inner.IndexOf('.');
            if (dot < 0)
            {
                return null;
            }
            string offsetPart = inner.Substring(0, dot);
            string rest = inner.Substring(dot + 1);

            // 类型字母后可带 '~' 反相（in_op INVERSE）
            bool tilde = rest.EndsWith("~", StringComparison.Ordinal);
            if (tilde)
            {
                indirectInverse = true;
                rest = rest[..^1];
            }

            // 先分离加数（+addend，位于掩码之后），再分离掩码（&mask），避免 '&0xff+4' 中 '+4' 被掩码吞掉
            int plus = rest.IndexOf('+');
            string addendPart = "0";
            if (plus >= 0)
            {
                addendPart = rest.Substring(plus + 1);
                rest = rest.Substring(0, plus);
            }

            // 掩码：(offset.type&mask[+addend])
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

        // 先分离字符串族类型修饰符（string/c、search/N、pstring/B、regex 标志），
        // 避免 '/' 被误当作掩码运算符（上游 '/' 仅出现在字符串族类型中）。
        // Split off string-family modifiers (string/c, search/N, pstring/B, regex flags) first,
        // so that '/' is not mistaken for a mask operator (upstream '/' only appears in string
        // family types).
        string typeTok = tokens[1];
        string baseType = typeTok;
        string modifiers = string.Empty;
        int slashIdx = typeTok.IndexOf('/');
        if (slashIdx >= 0)
        {
            baseType = typeTok.Substring(0, slashIdx);
            modifiers = typeTok.Substring(slashIdx + 1);
        }

        // 整数类型掩码运算（type&mask / type|mask / type^mask / type+mask / type-mask /
        // type*mask / type%mask）—— 字符串族类型无掩码。
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
                // 掩码可带 '~' 反相：byte&~0x80 → 读取值取反后再比较（上游 FILE_OPINVERSE）
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

        // 指令性 type：name（命名规则）/ use（引用命名规则）/ default（同层兜底）
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
            // default 指令（offset default x desc 形式）：仅当同层此前无规则命中时匹配
            string desc = tokens.Count > 3 ? ExtractDescription(tokens) : string.Empty;
            return new MagicRule(
                offset, offsetFromEnd, isContinuation,
                MagicValueType.None, integerWidth: 0, bigEndian: false, isUnsigned: false, isDate: false,
                MagicCompareOp.Any, Array.Empty<byte>(),
                desc, level, isDefault: true, noSpace: false);
        }

        // 其余指令性 type（skip/strength/clear/der 等）跳过
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
            // regex 修饰符：/c 忽略大小写、/s 匹配起点偏移
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
                // 按位与比较：匹配 (v & l) == l（上游 reln '&'）
                compareOp = MagicCompareOp.BitAnd;
                valueTok = valueTok.Substring(1);
            }
            else if (first == '^' && valueType == MagicValueType.Integer)
            {
                // 按位异或比较：匹配 (v & l) != l（上游 reln '^'）
                compareOp = MagicCompareOp.BitXor;
                valueTok = valueTok.Substring(1);
            }
        }

        // "x" 特殊值表示任意
        if (valueTok == "x")
        {
            compareOp = MagicCompareOp.Any;
        }

        // 解析 pattern
        byte[] pattern;
        string? regexPattern = null;
        if (valueType is MagicValueType.String or MagicValueType.Search or MagicValueType.PString)
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
            // regex: 保留原始字符串作为 pattern，并保存正则文本
            pattern = Encoding.UTF8.GetBytes(valueTok);
            regexPattern = valueTok;
        }

        // 解析描述模板（token[3..] 合并，空格分隔；前导 \b 表示 NOSPACE）
        string descriptionTemplate = string.Empty;
        bool noSpace = false;
        if (tokens.Count > 3)
        {
            descriptionTemplate = ExtractDescription(tokens);
            // 描述以 \b 开头 → NOSPACE（上游：\b 抑制前导空格，且 \b 本身被剥离）
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
    /// <para>解析字符串类型修饰符（string/c、search/N 等）：字母为标志，数字为搜索范围。</para>
    /// Parses string-type modifiers (string/c, search/N): letters are flags, digits are the range.
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
                    // 文本/二进制测试与偏移起点标志：纯托管引擎忽略
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
    /// <para>解析 pstring 修饰符（B/H/h/L/l/J 长度前缀）。</para>
    /// Parses pstring modifiers (B/H/h/L/l/J length prefixes).
    /// </summary>
    private static (MagicStringFlags flags, int range, int lengthBytes, bool bigEndian) ParsePStringModifiers(string modifiers)
    {
        MagicStringFlags flags = MagicStringFlags.None;
        int range = 0;
        int lengthBytes = 1;   // 默认 1 字节长度前缀（上游 PSTRING_1_LE）
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
                    // 长度包含前缀自身：匹配时按偏移处理
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
    /// <para>解析间接偏移的类型字母（上游 in_type 单字母编码）：l=lelong、L=belong、
    /// h/s=leshort、H/S=beshort、c/b/C/B=byte、m=melong、q=lequad、Q=bequad；
    /// 同时兼容完整类型名（belong/lelong 等）。</para>
    /// Parses the indirect-offset type letter (upstream in_type single-letter codes): l=lelong,
    /// L=belong, h/s=leshort, H/S=beshort, c/b/C/B=byte, m=melong, q=lequad, Q=bequad;
    /// full type names (belong/lelong ...) are also accepted.
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
    /// <para>判断是否为字符串族类型（string/search/pstring/regex，其 '/' 后为修饰符而非掩码）。</para>
    /// Determines whether the type is a string-family type (string/search/pstring/regex, whose '/'
    /// introduces modifiers rather than a mask).
    /// </summary>
    private static bool IsStringFamily(string type) =>
        type == "string" || type == "search" || type == "pstring" || type == "regex";

    /// <summary>
    /// <para>判断是否为整数类型（含 u 无符号前缀与 be/le 前缀变体）。</para>
    /// Determines whether the type is an integer type (including u/be/le prefix variants).
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
    /// <para>解析整数类型：返回字节宽度、字节序、无符号标志、日期标志与值类型。</para>
    /// Parses an integer type: returns byte width, endianness, unsigned flag, date flag and value type.
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
            // 间接偏移类型
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
        // 注意：ledate/bedate 前缀已被剥离为 date；qdate 等 8 字节日期暂按整数处理
        return (width, bigEndian, isUnsigned, isDate, MagicValueType.Integer);
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

    /// <summary>
    /// <para>查找掩码运算符位置（&amp;|^+-*/% 中的首个；类型名中不含这些字符）。</para>
    /// Finds the first mask-operator character (&amp;|^+-*/%; type names contain none of these).
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