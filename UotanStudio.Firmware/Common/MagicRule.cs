using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.Common;

/// <summary>
/// <para>文件类型匹配结果，包含类型描述与匹配规则的来源信息。</para>
/// File-type match result, carrying the type description and the source of the matched rule.
/// </summary>
public readonly struct MagicMatch : IEquatable<MagicMatch>
{
    /// <summary>
    /// <para>类型描述文本（如 "ELF 64-bit AArch64 executable"）。</para>
    /// Type description text (e.g. "ELF 64-bit AArch64 executable").
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// <para>匹配规则的来源标识（如 "embedded:firmware.magic" 或 "external:..."）。</para>
    /// Source identifier of the matched rule set.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// <para>是否命中。</para>
    /// Whether the file matched.
    /// </summary>
    public bool IsMatch { get; }

    /// <summary>
    /// <para>以描述文本推导匹配状态创建匹配结果。</para>
    /// Creates a match whose <see cref="IsMatch"/> derives from a non-empty description.
    /// </summary>
    public MagicMatch(string description, string source)
        : this(description, source, !string.IsNullOrEmpty(description))
    {
    }

    /// <summary>
    /// <para>以显式匹配状态创建匹配结果（用于顶层规则命中但无描述文本的场景）。</para>
    /// Creates a match with an explicit <see cref="IsMatch"/> (for top-level rule hits without text).
    /// </summary>
    public MagicMatch(string description, string source, bool isMatch)
    {
        Description = description;
        Source = source;
        IsMatch = isMatch;
    }

    /// <inheritdoc />
    public bool Equals(MagicMatch other) => Description == other.Description && Source == other.Source;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MagicMatch other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Description, Source);

    /// <summary>
    /// <para>无匹配哨兵值（<see cref="IsMatch"/> 为 <see langword="false"/>）。</para>
    /// No-match sentinel (<see cref="IsMatch"/> is <see langword="false"/>).
    /// </summary>
    public static MagicMatch None => new(string.Empty, string.Empty, false);

    /// <summary>
    /// <para>从字符串隐式转换，便于在硬编码快速路径中复用。</para>
    /// Implicit conversion from string for reuse in the hardcoded fast-path.
    /// </summary>
    public static implicit operator MagicMatch(string? description) =>
        string.IsNullOrEmpty(description) ? None : new MagicMatch(description, "builtin", true);
}

/// <summary>
/// <para>比较运算符类型。对应上游 file(1) 的 <c>=</c>、<c>!</c>、<c>&lt;</c>、<c>&gt;</c>、<c>x</c>、
/// <c>&amp;</c>（按位与，匹配 <c>(v &amp; l) == l</c>）与 <c>^</c>（按位异或，匹配 <c>(v &amp; l) != l</c>）。</para>
/// Comparison operator type, matching file(1)'s <c>=</c>, <c>!</c>, <c>&lt;</c>, <c>&gt;</c>, <c>x</c>,
/// <c>&amp;</c> (bitwise AND: <c>(v &amp; l) == l</c>) and <c>^</c> (bitwise XOR: <c>(v &amp; l) != l</c>).
/// </summary>
public enum MagicCompareOp
{
    /// <summary>相等（默认）</summary>
    Equal,
    /// <summary>不等</summary>
    NotEqual,
    /// <summary>小于</summary>
    LessThan,
    /// <summary>大于</summary>
    GreaterThan,
    /// <summary>任意值（存在即可）</summary>
    Any,
    /// <summary>按位与：匹配 (actual &amp; test) == test（上游 reln '&amp;'）</summary>
    BitAnd,
    /// <summary>按位异或：匹配 (actual &amp; test) != test（上游 reln '^'）</summary>
    BitXor,
}

/// <summary>
/// <para>掩码运算类型（type&amp;mask 写法中的运算符，对应上游 <c>FILE_OP*</c> 与 magic(5) 的
/// <c>&amp;</c>/<c>|</c>/<c>^</c>/<c>+</c>/<c>-</c>/<c>*</c>/<c>/</c>/<c>%</c>）。</para>
/// Mask operation type (the operator in type&amp;mask syntax, mapping to upstream <c>FILE_OP*</c>).
/// </summary>
public enum MagicMaskOp
{
    /// <summary>无掩码运算</summary>
    None,
    /// <summary>按位与（&amp;）</summary>
    And,
    /// <summary>按位或（|）</summary>
    Or,
    /// <summary>按位异或（^）</summary>
    Xor,
    /// <summary>加法（+）</summary>
    Add,
    /// <summary>减法（-）</summary>
    Subtract,
    /// <summary>乘法（*）</summary>
    Multiply,
    /// <summary>除法（/）</summary>
    Divide,
    /// <summary>取模（%）</summary>
    Modulo,
}

/// <summary>
/// <para>字符串类型修饰符（string/c、search/N、pstring 长度前缀等），对应上游 str_flags。</para>
/// String type modifiers (string/c, search/N, pstring length prefixes), mapping to upstream str_flags.
/// </summary>
[Flags]
public enum MagicStringFlags
{
    /// <summary>无修饰</summary>
    None = 0,
    /// <summary>忽略大小写（/c、/C）</summary>
    IgnoreCase = 1,
    /// <summary>压缩空白：模式中空白可匹配一个或多个空白（/W）</summary>
    CompactWhitespace = 2,
    /// <summary>可选空白：模式中空白可匹配零个或多个空白（/w）</summary>
    OptionalWhitespace = 4,
    /// <summary>修剪字符串尾部空白（/T）</summary>
    Trim = 8,
    /// <summary>整词匹配：匹配后须为空白或结束（/f）</summary>
    FullWord = 16,
}

/// <summary>
/// <para>匹配值类型。对应上游 file(1) 的 <c>byte</c>/<c>short</c>/<c>long</c>/<c>string</c>/
/// <c>search</c>/<c>regex</c>/<c>pstring</c> 等。</para>
/// Match value type, corresponding to file(1)'s <c>byte</c>/<c>short</c>/<c>long</c>/<c>string</c>/
/// <c>search</c>/<c>regex</c>/<c>pstring</c>.
/// </summary>
public enum MagicValueType
{
    /// <summary>无类型（仅检查存在性 / default）</summary>
    None,
    /// <summary>ASCII/UTF-8 字符串（支持 \xNN 转义与 /c /W 等修饰符）</summary>
    String,
    /// <summary>字节序敏感的整数：byte/short/long/quad/leid/leshort 等</summary>
    Integer,
    /// <summary>正则匹配（对应上游 regex 类型）</summary>
    Regex,
    /// <summary>范围搜索（对应上游 search 类型，search/N 限定范围）</summary>
    Search,
    /// <summary>长度前缀字符串（对应上游 pstring，B/H/h/L/l/J 长度前缀）</summary>
    PString,
}

/// <summary>
/// <para>单条 magic 规则的匹配条件与描述模板。</para>
/// Match condition and description template for a single magic rule.
/// </summary>
public sealed class MagicRule
{
    /// <summary>
    /// <para>匹配偏移。当 <see cref="IsContinuation"/> 为真时，表示相对上一层规则匹配结束位置的偏移。</para>
    /// Match offset. When <see cref="IsContinuation"/> is set, it is relative to the end of the
    /// previous level's match.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// <para>偏移是否为负（从文件末尾向前数）。</para>
    /// Whether the offset is negative (counted from the end of the file).
    /// </summary>
    public bool OffsetFromEnd { get; }

    /// <summary>
    /// <para>续接偏移（上游 '&amp;' 前缀，OFFADD）：偏移相对上一层规则匹配结束位置。</para>
    /// Continuation offset (upstream '&amp;' prefix, OFFADD): relative to the end of the previous
    /// level's match.
    /// </summary>
    public bool IsContinuation { get; }

    /// <summary>
    /// <para>匹配值类型。</para>
    /// Match value type.
    /// </summary>
    public MagicValueType ValueType { get; }

    /// <summary>
    /// <para>整数类型字节宽度（1/2/4/8）。</para>
    /// Integer type byte width (1/2/4/8).
    /// </summary>
    public int IntegerWidth { get; }

    /// <summary>
    /// <para>整数是否大端。</para>
    /// Whether the integer is big-endian.
    /// </summary>
    public bool BigEndian { get; }

    /// <summary>
    /// <para>无符号整数（上游 'u' 前缀，如 ubyte/ulelong），影响 &lt; / &gt; 比较与数值输出。</para>
    /// Unsigned integer (upstream 'u' prefix, e.g. ubyte/ulelong), affects &lt; / &gt; comparisons.
    /// </summary>
    public bool IsUnsigned { get; }

    /// <summary>
    /// <para>日期类型（date/ledate/bedate 等）：描述中的 %d 会格式化为日期字符串。</para>
    /// Date type (date/ledate/bedate): %d in the description is formatted as a date string.
    /// </summary>
    public bool IsDate { get; }

    /// <summary>
    /// <para>比较运算符。</para>
    /// Comparison operator.
    /// </summary>
    public MagicCompareOp CompareOp { get; }

    /// <summary>
    /// <para>匹配模式字节（字符串/整数值按大端存储）。</para>
    /// Match pattern bytes (string pattern, or integer value stored big-endian).
    /// </summary>
    public byte[] Pattern { get; }

    /// <summary>
    /// <para>整数掩码（type&amp;mask 写法）。<see cref="HasMask"/> 为 false 时无意义。</para>
    /// Integer mask (type&amp;mask syntax). Meaningless when <see cref="HasMask"/> is false.
    /// </summary>
    public long Mask { get; }

    /// <summary>
    /// <para>是否存在掩码。</para>
    /// Whether a mask is present.
    /// </summary>
    public bool HasMask { get; }

    /// <summary>
    /// <para>掩码运算类型（type&amp;mask / type|mask / type^mask 等）。</para>
    /// Mask operation type (type&amp;mask / type|mask / type^mask ...).
    /// </summary>
    public MagicMaskOp MaskOp { get; }

    /// <summary>
    /// <para>掩码是否反相（上游 '~'：对读取值取反后再比较）。</para>
    /// Whether the value is complemented (upstream '~': ~value before comparison).
    /// </summary>
    public bool MaskInverse { get; }

    /// <summary>
    /// <para>字符串修饰符。</para>
    /// String modifiers.
    /// </summary>
    public MagicStringFlags StringFlags { get; }

    /// <summary>
    /// <para>search 类型搜索范围（search/N；0 表示默认 100 字节）。</para>
    /// Search range for the search type (search/N; 0 = default 100 bytes).
    /// </summary>
    public int SearchRange { get; }

    /// <summary>
    /// <para>pstring 长度前缀字节数（1/2/4；0 表示非 pstring）。</para>
    /// PString length-prefix byte count (1/2/4; 0 = not a pstring).
    /// </summary>
    public int PStringLengthBytes { get; }

    /// <summary>
    /// <para>pstring 长度前缀是否大端。</para>
    /// Whether the pstring length prefix is big-endian.
    /// </summary>
    public bool PStringBigEndian { get; }

    /// <summary>
    /// <para>regex 原始模式文本。</para>
    /// Raw regex pattern text.
    /// </summary>
    public string? RegexPattern { get; }

    /// <summary>
    /// <para>描述模板（支持 %d/%x/%s/%c 等占位符）。</para>
    /// Description template (supports %d/%x/%s/%c placeholders).
    /// </summary>
    public string DescriptionTemplate { get; }

    /// <summary>
    /// <para>缩进级别（'&gt;' 数量）。</para>
    /// Indent level (number of '&gt;').
    /// </summary>
    public int Indent { get; }

    /// <summary>
    /// <para>是否为 default 指令（仅当同层此前无规则命中时才匹配）。</para>
    /// Whether this is a default directive (matches only when no previous sibling matched).
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// <para>命名规则名（name 指令定义，供 use 引用）。</para>
    /// Named-rule name (defined by the name directive, referenced by use).
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// <para>use 指令引用的命名规则名。</para>
    /// Named-rule name referenced by the use directive.
    /// </summary>
    public string? UseName { get; }

    /// <summary>
    /// <para>描述是否禁止前导空格（上游 NOSPACE，描述以 \b 开头）。</para>
    /// Whether the description suppresses a leading space (upstream NOSPACE, description starts with \b).
    /// </summary>
    public bool NoSpace { get; }

    /// <summary>
    /// <para>是否为间接偏移（(type.offset[&amp;mask][+addend]) 写法）。</para>
    /// Whether this is an indirect offset ((type.offset[&amp;mask][+addend]) syntax).
    /// </summary>
    public bool IsIndirect { get; }

    /// <summary>
    /// <para>间接偏移基准读取宽度。</para>
    /// Indirect offset base read width.
    /// </summary>
    public int IndirectBaseWidth { get; }

    /// <summary>
    /// <para>间接偏移基准读取是否大端。</para>
    /// Whether the indirect offset base read is big-endian.
    /// </summary>
    public bool IndirectBigEndian { get; }

    /// <summary>
    /// <para>间接偏移加数（addend）。</para>
    /// Indirect offset addend.
    /// </summary>
    public int IndirectAddend { get; }

    /// <summary>
    /// <para>间接偏移掩码（(type.offset&amp;mask) 写法）。</para>
    /// Indirect offset mask ((type.offset&amp;mask) syntax).
    /// </summary>
    public long IndirectMask { get; }

    /// <summary>
    /// <para>间接偏移是否存在掩码。</para>
    /// Whether the indirect offset has a mask.
    /// </summary>
    public bool IndirectHasMask { get; }

    /// <summary>
    /// <para>间接偏移读取值是否反相（上游 '~' 于间接偏移内）。</para>
    /// Whether the indirect offset value is complemented (upstream '~' inside indirect offsets).
    /// </summary>
    public bool IndirectInverse { get; }

    private Regex? _compiledRegex;

    internal MagicRule(
        int offset, bool offsetFromEnd, bool isContinuation,
        MagicValueType valueType, int integerWidth, bool bigEndian, bool isUnsigned, bool isDate,
        MagicCompareOp compareOp, byte[] pattern,
        string descriptionTemplate, int indent,
        bool isDefault, bool noSpace,
        string? name = null, string? useName = null,
        bool isIndirect = false, int indirectBaseWidth = 0, bool indirectBigEndian = false, int indirectAddend = 0,
        long indirectMask = 0, bool indirectHasMask = false, bool indirectInverse = false,
        long mask = 0, bool hasMask = false, MagicMaskOp maskOp = MagicMaskOp.None,
        bool maskInverse = false,
        MagicStringFlags stringFlags = MagicStringFlags.None, int searchRange = 0,
        int pstringLengthBytes = 0, bool pstringBigEndian = false,
        string? regexPattern = null)
    {
        Offset = offset;
        OffsetFromEnd = offsetFromEnd;
        IsContinuation = isContinuation;
        ValueType = valueType;
        IntegerWidth = integerWidth;
        BigEndian = bigEndian;
        IsUnsigned = isUnsigned;
        IsDate = isDate;
        CompareOp = compareOp;
        Pattern = pattern;
        DescriptionTemplate = descriptionTemplate;
        Indent = indent;
        IsDefault = isDefault;
        NoSpace = noSpace;
        Name = name;
        UseName = useName;
        IsIndirect = isIndirect;
        IndirectBaseWidth = indirectBaseWidth;
        IndirectBigEndian = indirectBigEndian;
        IndirectAddend = indirectAddend;
        IndirectMask = indirectMask;
        IndirectHasMask = indirectHasMask;
        IndirectInverse = indirectInverse;
        Mask = mask;
        HasMask = hasMask;
        MaskOp = maskOp;
        MaskInverse = maskInverse;
        StringFlags = stringFlags;
        SearchRange = searchRange;
        PStringLengthBytes = pstringLengthBytes;
        PStringBigEndian = pstringBigEndian;
        RegexPattern = regexPattern;
    }

    /// <summary>
    /// <para>尝试在给定文件字节范围上匹配本规则（顶层入口，无续接基准偏移）。</para>
    /// Attempts to match this rule against the provided file data (top-level entry, no
    /// continuation base offset).
    /// </summary>
    public string? TryMatch(byte[] fileData, int fileLength)
    {
        return TryMatchAtOffset(fileData, fileLength, 0, 0, out _);
    }

    /// <summary>
    /// <para>以指定基准偏移尝试匹配：<paramref name="continuationBase"/> 为上一层规则匹配结束位置，
    /// 仅当 <see cref="IsContinuation"/> 时参与计算。</para>
    /// Attempts to match with a base offset: <paramref name="continuationBase"/> is the end of the
    /// previous level's match, used only when <see cref="IsContinuation"/> is set.
    /// </summary>
    internal string? TryMatchAtOffset(byte[] fileData, int fileLength, int continuationBase, int baseIndirectOffset, out int matchEnd)
    {
        matchEnd = 0;
        int offset;

        if (IsIndirect)
        {
            // 间接偏移：(type.offset[&mask][+addend]) — 从 baseIndirectOffset 处读取基准值
            int readBase = OffsetFromEnd ? fileLength + Offset : Offset;
            if (readBase < 0 || readBase + IndirectBaseWidth > fileData.Length)
            {
                return null;
            }
            long raw = ReadInteger(fileData, readBase, IndirectBaseWidth, IndirectBigEndian);
            if (IndirectHasMask)
            {
                raw &= IndirectMask;
            }
            if (IndirectInverse)
            {
                raw = ~raw;
            }
            offset = (int)(raw + IndirectAddend);
            if (offset < 0)
            {
                return null;
            }
        }
        else if (IsContinuation)
        {
            // 上游 '&' 续接偏移：相对于上一层规则匹配结束位置
            offset = continuationBase + Offset;
        }
        else if (OffsetFromEnd)
        {
            offset = fileLength + Offset;
        }
        else
        {
            offset = Offset;
        }

        if (offset < 0 || offset > fileData.Length)
        {
            return null;
        }

        string? desc = TryMatchAt(fileData, offset, fileLength, out matchEnd);
        return desc;
    }

    private string? TryMatchAt(byte[] fileData, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset;

        // default 指令：无条件匹配（同层未命中判定由 MagicRuleSet 负责）
        if (IsDefault)
        {
            return FormatDescription(fileData, offset, fileLength, intValue: null, strValue: null);
        }

        if (CompareOp == MagicCompareOp.Any)
        {
            // x：任意值，仅需保证该位置可读
            if (offset >= fileData.Length)
            {
                return null;
            }
            // 字符串族：仍需读取值供 %s 占位符输出（上游 mprint 对 reln x 打印匹配到的字符串）
            if (ValueType == MagicValueType.String)
            {
                int len = 0;
                while (offset + len < fileData.Length && !IsSpace(fileData[offset + len]) && fileData[offset + len] != 0)
                {
                    len++;
                }
                matchEnd = offset + len;
                return FormatDescription(fileData, offset, fileLength, intValue: null, strValue: MatchStringValue(fileData, offset, len));
            }
            if (ValueType == MagicValueType.PString)
            {
                // pstring x：读取长度前缀并输出字符串（空 pattern 必然匹配）
                return TryMatchPString(fileData, offset, fileLength, out matchEnd);
            }
            // 整数/日期：读取值供 %d/%s（日期格式化）占位符输出
            if (ValueType == MagicValueType.Integer && offset + IntegerWidth <= fileData.Length)
            {
                long v = ReadInteger(fileData, offset, IntegerWidth, BigEndian);
                matchEnd = offset + IntegerWidth;
                return FormatDescription(fileData, offset, fileLength, intValue: v, strValue: null);
            }
            matchEnd = offset + Math.Max(1, IntegerWidth);
            return FormatDescription(fileData, offset, fileLength, intValue: null, strValue: null);
        }

        switch (ValueType)
        {
            case MagicValueType.String:
                return TryMatchString(fileData, offset, fileLength, out matchEnd);
            case MagicValueType.Search:
                return TryMatchSearch(fileData, offset, fileLength, out matchEnd);
            case MagicValueType.PString:
                return TryMatchPString(fileData, offset, fileLength, out matchEnd);
            case MagicValueType.Regex:
                return TryMatchRegex(fileData, offset, fileLength, out matchEnd);
            case MagicValueType.Integer:
                return TryMatchInteger(fileData, offset, fileLength, out matchEnd);
            default:
                return null;
        }
    }

    // ====== 字符串匹配 ======

    private string? TryMatchString(byte[] data, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset;
        int len = Pattern.Length;
        if (offset + len > data.Length)
        {
            return null;
        }

        bool ignoreCase = (StringFlags & MagicStringFlags.IgnoreCase) != 0;
        bool compactWs = (StringFlags & MagicStringFlags.CompactWhitespace) != 0;
        bool optionalWs = (StringFlags & MagicStringFlags.OptionalWhitespace) != 0;
        bool fullWord = (StringFlags & MagicStringFlags.FullWord) != 0;

        int i = 0;
        int j = 0;
        while (i < len)
        {
            byte p = Pattern[i];
            byte d = (j < data.Length) ? data[offset + j] : (byte)0;

            if (compactWs && IsSpace(p))
            {
                // 模式中的空白匹配文件中的 1..n 个空白
                if (!IsSpace(d))
                {
                    return null;
                }
                while (j < data.Length && IsSpace(data[offset + j]))
                {
                    j++;
                }
                i++;
                continue;
            }
            if (optionalWs && IsSpace(p))
            {
                // 模式中的空白匹配文件中的 0..n 个空白
                while (j < data.Length && IsSpace(data[offset + j]))
                {
                    j++;
                }
                i++;
                continue;
            }

            byte cmp = d;
            // 上游 file_strncmp：pattern 为小写时比较 tolower(文件字节)，为大写时比较 toupper(文件字节)
            // Upstream file_strncmp: lowercase pattern → tolower(file byte); uppercase pattern → toupper(file byte).
            if (ignoreCase && p is >= (byte)'a' and <= (byte)'z')
            {
                cmp = ToLowerAscii(cmp);
            }
            else if (ignoreCase && p is >= (byte)'A' and <= (byte)'Z')
            {
                cmp = ToUpperAscii(cmp);
            }

            if (cmp != p)
            {
                return null;
            }
            i++;
            j++;
        }

        if (fullWord && j < data.Length && !IsSpace(data[offset + j]))
        {
            return null;
        }

        // 应用字符串关系运算符（上游 file_strncmp 语义）：= 相等、!= 不等、> 大于、< 小于
        // Apply string relation operators (upstream file_strncmp semantics).
        if (CompareOp == MagicCompareOp.NotEqual)
        {
            return null;   // 精确相等时 != 不命中（已走到此处说明模式匹配）
        }
        if (CompareOp is MagicCompareOp.LessThan or MagicCompareOp.GreaterThan)
        {
            int diff = CompareStrings(data, offset, fileLength);
            if (CompareOp == MagicCompareOp.LessThan ? diff >= 0 : diff <= 0)
            {
                return null;
            }
        }

        matchEnd = offset + j;
        return FormatDescription(data, offset, fileLength, intValue: null, strValue: MatchStringValue(data, offset, j));
    }

    /// <summary>
    /// <para>字符串比较：用于 &lt;/&gt; 等关系运算符（上游 file_strncmp 语义：返回首个差异处比较值）。</para>
    /// String comparison for &lt;/&gt; relations (upstream file_strncmp semantics).
    /// </summary>
    private int CompareStrings(byte[] data, int offset, int fileLength)
    {
        int len = Pattern.Length;
        for (int i = 0; i < len; i++)
        {
            byte p = Pattern[i];
            byte d = (offset + i < data.Length) ? data[offset + i] : (byte)0;
            byte cmp = d;
            if ((StringFlags & MagicStringFlags.IgnoreCase) != 0)
            {
                cmp = ToLowerAscii(cmp);
                p = ToLowerAscii(p);
            }
            int diff = cmp - p;
            if (diff != 0)
            {
                return diff;
            }
        }
        return 0;
    }

    // ====== search 范围搜索 ======

    private string? TryMatchSearch(byte[] data, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset;
        int range = SearchRange > 0 ? SearchRange : 100;
        int limit = Math.Min(data.Length, offset + range);
        int len = Pattern.Length;
        if (len == 0)
        {
            return null;
        }
        for (int start = offset; start + len <= limit; start++)
        {
            bool ok = true;
            for (int k = 0; k < len; k++)
            {
                byte p = Pattern[k];
                byte d = data[start + k];
                if ((StringFlags & MagicStringFlags.IgnoreCase) != 0)
                {
                    p = ToLowerAscii(p);
                    d = ToLowerAscii(d);
                }
                if (d != p)
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                matchEnd = start + len;
                return FormatDescription(data, start, fileLength, intValue: null, strValue: MatchStringValue(data, start, len));
            }
        }
        return null;
    }

    // ====== pstring 长度前缀字符串 ======

    private string? TryMatchPString(byte[] data, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset;
        int lenBytes = PStringLengthBytes;
        if (lenBytes is not (1 or 2 or 4))
        {
            // 未指定长度前缀时按 1 字节处理（上游默认 PSTRING_1_LE）
            lenBytes = 1;
        }
        if (offset + lenBytes > data.Length)
        {
            return null;
        }
        long strLen = ReadInteger(data, offset, lenBytes, PStringBigEndian);
        if (strLen < 0 || offset + lenBytes + strLen > data.Length)
        {
            return null;
        }
        int start = offset + lenBytes;
        int len = (int)strLen;
        for (int k = 0; k < Pattern.Length; k++)
        {
            byte p = Pattern[k];
            if (k >= len)
            {
                // 模式比实际字符串长：允许以 0 填充对齐（上游以 vallen 比较）
                p = 0;
            }
            byte d = data[start + k];
            if ((StringFlags & MagicStringFlags.IgnoreCase) != 0)
            {
                p = ToLowerAscii(p);
                d = ToLowerAscii(d);
            }
            if (d != p)
            {
                return null;
            }
        }
        matchEnd = start + len;
        return FormatDescription(data, start, fileLength, intValue: null, strValue: MatchStringValue(data, start, len));
    }

    // ====== regex 正则匹配 ======

    private string? TryMatchRegex(byte[] data, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset;
        if (string.IsNullOrEmpty(RegexPattern))
        {
            return null;
        }
        Regex rx = _compiledRegex ??= CompileRegex();
        // 以 Latin-1 逐字节解码以保持二进制匹配语义（与上游 POSIX regex 一致）
        string region = Encoding.Latin1.GetString(data, offset, data.Length - offset);
        Match m = rx.Match(region);
        if (!m.Success)
        {
            return null;
        }
        matchEnd = offset + m.Index + m.Length;
        return FormatDescription(data, offset + m.Index, fileLength, intValue: null, strValue: m.Value);
    }

    private Regex CompileRegex()
    {
        RegexOptions options = RegexOptions.CultureInvariant;
        if ((StringFlags & MagicStringFlags.IgnoreCase) != 0)
        {
            options |= RegexOptions.IgnoreCase;
        }
        try
        {
            return new Regex(RegexPattern!, options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return new Regex(Regex.Escape(RegexPattern ?? string.Empty), options);
        }
    }

    // ====== 整数匹配 ======

    private string? TryMatchInteger(byte[] data, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset + IntegerWidth;
        if (offset + IntegerWidth > data.Length)
        {
            return null;
        }
        long actual = ReadInteger(data, offset, IntegerWidth, BigEndian);

        // 应用 type&mask（上游 DO_CVT1：v op= num_mask，随后若 INVERSE 则 v = ~v）
        if (HasMask)
        {
            actual = ApplyMaskOp(actual, MaskOp, Mask);
        }
        if (MaskInverse)
        {
            // 反相按字段宽度截断（上游 p->fld = ~p->fld，fld 为对应宽度的整数类型）
            actual = ~actual & WidthMask(IntegerWidth);
        }

        long expected = ReadInteger(Pattern, 0, Pattern.Length, bigEndian: true);

        bool matched = CompareOp switch
        {
            MagicCompareOp.Equal => actual == expected,
            MagicCompareOp.NotEqual => actual != expected,
            MagicCompareOp.LessThan => IsUnsigned ? (ulong)actual < (ulong)expected : actual < expected,
            MagicCompareOp.GreaterThan => IsUnsigned ? (ulong)actual > (ulong)expected : actual > expected,
            MagicCompareOp.BitAnd => (actual & expected) == expected,
            MagicCompareOp.BitXor => (actual & expected) != expected,
            MagicCompareOp.Any => true,
            _ => false
        };
        if (!matched)
        {
            return null;
        }
        return FormatDescription(data, offset, fileLength, intValue: actual, strValue: null);
    }

    private static long ApplyMaskOp(long value, MagicMaskOp op, long mask)
    {
        return op switch
        {
            MagicMaskOp.And => value & mask,
            MagicMaskOp.Or => value | mask,
            MagicMaskOp.Xor => value ^ mask,
            MagicMaskOp.Add => value + mask,
            MagicMaskOp.Subtract => value - mask,
            MagicMaskOp.Multiply => value * mask,
            MagicMaskOp.Divide => mask != 0 ? value / mask : value,
            MagicMaskOp.Modulo => mask != 0 ? value % mask : value,
            _ => value
        };
    }

    /// <summary>
    /// <para>按字段宽度生成全 1 掩码（byte→0xff、short→0xffff、long→0xffffffff、quad→全 64 位）。</para>
    /// Builds an all-ones mask for the field width (byte→0xff, short→0xffff, long→0xffffffff,
    /// quad→full 64-bit).
    /// </summary>
    private static long WidthMask(int width) =>
        width >= 8 ? -1L : (1L << (width * 8)) - 1L;

    private static long ReadInteger(byte[] data, int offset, int width, bool bigEndian)
    {
        if (width == 8)
        {
            return ReadInt64(data, offset, bigEndian);
        }
        long v = 0;
        if (bigEndian)
        {
            for (int i = 0; i < width; i++)
            {
                v = (v << 8) | data[offset + i];
            }
        }
        else
        {
            for (int i = width - 1; i >= 0; i--)
            {
                v = (v << 8) | data[offset + i];
            }
        }
        return v;
    }

    private static long ReadInt64(byte[] data, int offset, bool bigEndian)
    {
        if (bigEndian)
        {
            return ((long)data[offset] << 56) | ((long)data[offset + 1] << 48)
                   | ((long)data[offset + 2] << 40) | ((long)data[offset + 3] << 32)
                   | ((long)data[offset + 4] << 24) | ((long)data[offset + 5] << 16)
                   | ((long)data[offset + 6] << 8) | data[offset + 7];
        }
        return ((long)data[offset + 7] << 56) | ((long)data[offset + 6] << 48)
               | ((long)data[offset + 5] << 40) | ((long)data[offset + 4] << 32)
               | ((long)data[offset + 3] << 24) | ((long)data[offset + 2] << 16)
               | ((long)data[offset + 1] << 8) | data[offset];
    }

    // ====== 描述格式化 ======

    private string FormatDescription(byte[] data, int offset, int fileLength, long? intValue, string? strValue)
    {
        if (string.IsNullOrEmpty(DescriptionTemplate))
        {
            if (ValueType == MagicValueType.Integer && intValue.HasValue)
            {
                return IsDate
                    ? FormatDate(intValue.Value)
                    : intValue.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrEmpty(strValue))
            {
                return strValue;
            }
            return string.Empty;
        }
        return SubstitutePlaceholders(DescriptionTemplate, intValue, strValue, IsDate);
    }

    /// <summary>
    /// <para>将 32/64 位秒级时间戳格式化为日期字符串（对应上游 file_fmttime 的
    /// <c>YYYY-MM-DD HH:MM:SS</c> 形式，UTC）。</para>
    /// Formats a 32/64-bit epoch value as a date string (upstream file_fmttime, UTC).
    /// </summary>
    private static string FormatDate(long value)
    {
        try
        {
            long secs = value;
            DateTimeOffset dt = secs >= 0
                ? DateTimeOffset.FromUnixTimeSeconds(secs)
                : DateTimeOffset.FromUnixTimeMilliseconds(0).AddSeconds(secs);
            return dt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string SubstitutePlaceholders(string template, long? intValue, string? strValue, bool isDate)
    {
        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // 跳过宽度/标志（仅数字与 '-'）
            int j = i + 1;
            int width = 0;
            bool leftAlign = false;
            while (j < template.Length && (template[j] == '-' || char.IsDigit(template[j])))
            {
                if (template[j] == '-')
                {
                    leftAlign = true;
                }
                else
                {
                    width = width * 10 + (template[j] - '0');
                }
                j++;
            }

            if (j >= template.Length)
            {
                sb.Append('%');
                i++;
                continue;
            }

            char spec = template[j];
            string segment;
            if (isDate && (spec == 'd' || spec == 's'))
            {
                segment = intValue.HasValue ? FormatDate(intValue.Value) : "?";
            }
            else
            {
                segment = spec switch
                {
                    's' => strValue ?? intValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    'd' or 'i' => intValue?.ToString(CultureInfo.InvariantCulture) ?? "0",
                    'x' => (intValue ?? 0).ToString("x", CultureInfo.InvariantCulture),
                    'X' => (intValue ?? 0).ToString("X", CultureInfo.InvariantCulture),
                    'c' => intValue.HasValue && intValue.Value >= 0 && intValue.Value <= 0xFF
                        ? ((char)intValue.Value).ToString()
                        : "?",
                    '%' => "%",
                    _ => "%" + spec
                };
            }

            if (width > 0 && spec != '%')
            {
                segment = leftAlign ? segment.PadRight(width) : segment.PadLeft(width);
            }

            sb.Append(segment);
            i = j + 1;
        }
        return sb.ToString();
    }

    private static bool IsSpace(byte b) =>
        b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\v' or (byte)'\f';

    private static byte ToLowerAscii(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private static byte ToUpperAscii(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;

    /// <summary>
    /// <para>取匹配到的字符串值（去除尾部空白），用于 %s 占位符输出。</para>
    /// Gets the matched string value (trimmed) for %s substitution.
    /// </summary>
    private static string MatchStringValue(byte[] data, int offset, int length)
    {
        int end = length;
        while (end > 0 && IsSpace(data[offset + end - 1]))
        {
            end--;
        }
        return Encoding.UTF8.GetString(data, offset, end);
    }
}

/// <summary>
/// <para>magic 规则集：包含顶层规则与子规则（按缩进组织），支持按文件头匹配。</para>
/// A magic rule set with top-level rules and indented sub-rules, capable of matching against file headers.
/// </summary>
public sealed class MagicRuleSet
{
    private readonly List<MagicRule> _topLevel = new();
    private readonly Dictionary<MagicRule, List<MagicRule>> _children = new();
    private readonly Dictionary<string, MagicRule> _named = new(StringComparer.Ordinal);
    private readonly string _source;

    public string Source => _source;

    public MagicRuleSet(string source)
    {
        _source = source ?? "unknown";
    }

    public void Add(MagicRule rule, MagicRule? parent)
    {
        if (parent is null)
        {
            _topLevel.Add(rule);
        }
        else
        {
            if (!_children.TryGetValue(parent, out var list))
            {
                list = new List<MagicRule>();
                _children[parent] = list;
            }
            list.Add(rule);
        }

        // 命名规则登记：name X 指令创建的规则（含其子规则体）供 use 引用
        if (rule.Name is not null)
        {
            _named[rule.Name] = rule;
        }
    }

    /// <summary>
    /// <para>对文件数据执行匹配，返回首个命中的顶层规则及其递归子规则拼接的描述。
    /// 与上游 file_softmagic 一致：子规则可任意层级嵌套（&gt;&gt;/&gt;&gt;&gt;），
    /// 逐层在父规则匹配结束位置（'&amp;' 续接偏移）或绝对偏移处尝试匹配。</para>
    /// Matches the file data, returning the first top-level rule hit with its recursively matched
    /// sub-rule descriptions concatenated. Sub-rules may nest arbitrarily deep (&gt;&gt;/&gt;&gt;&gt;),
    /// each matched at the parent match end ('&amp;' continuation offset) or at an absolute offset.
    /// </summary>
    public MagicMatch Match(byte[] fileData, int fileLength)
    {
        foreach (var rule in _topLevel)
        {
            var parts = new List<string>();
            if (TryMatchTree(rule, fileData, fileLength, 0, 0, parts))
            {
                // 顶层规则命中即视为匹配（与上游 file_softmagic 一致），即便未产生任何描述文本。
                // A hit on a top-level rule counts as a match (like upstream file_softmagic), even if no
                // description text was produced.
                return new MagicMatch(string.Join(string.Empty, parts), _source, isMatch: true);
            }
        }
        return MagicMatch.None;
    }

    /// <summary>
    /// <para>递归匹配单条规则及其子树：命中后追加描述（遵守 NOSPACE），再以匹配结束位置为
    /// 基准尝试全部子规则；default 子规则仅当同层此前无命中时才参与。</para>
    /// Recursively matches one rule and its sub-tree: on hit appends the description (honoring
    /// NOSPACE), then tries all children using the match end as continuation base; default children
    /// only apply when no previous sibling at the same level matched.
    /// </summary>
    private bool TryMatchTree(MagicRule rule, byte[] fileData, int fileLength, int continuationBase, int baseIndirectOffset, List<string> parts)
    {
        // name 指令：命名规则本身不匹配，其子规则体在 "use" 时被引用（透明分组）
        if (rule.Name is not null)
        {
            return TryMatchNamedBody(rule, fileData, fileLength, continuationBase, baseIndirectOffset, parts);
        }

        // use 指令：展开引用命名规则，在当前偏移处匹配其子规则体
        if (rule.UseName is not null)
        {
            if (!_named.TryGetValue(rule.UseName, out var namedRule))
            {
                return false;
            }
            int namedBase = rule.IsContinuation ? continuationBase + rule.Offset : rule.Offset;
            return TryMatchNamedBody(namedRule, fileData, fileLength, namedBase, baseIndirectOffset, parts);
        }

        string? desc = rule.TryMatchAtOffset(fileData, fileLength, continuationBase, baseIndirectOffset, out int matchEnd);
        if (desc is null)
        {
            return false;
        }

        AppendPart(parts, desc, rule.NoSpace);

        if (_children.TryGetValue(rule, out var children))
        {
            bool siblingMatched = false;
            foreach (var child in children)
            {
                // default 指令：仅当同层此前无任何规则命中时才匹配（上游 got_match 语义）
                if (child.IsDefault)
                {
                    if (siblingMatched)
                    {
                        continue;
                    }
                }

                int childBase = child.IsContinuation ? matchEnd : 0;
                if (TryMatchTree(child, fileData, fileLength, childBase, matchEnd, parts))
                {
                    siblingMatched = true;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// <para>匹配命名规则（name 指令）的子规则体：任一子规则命中即视为整体命中，描述拼接到输出。</para>
    /// Matches the sub-rule body of a named rule (name directive): any child hit counts as a hit and
    /// its descriptions are appended.
    /// </summary>
    private bool TryMatchNamedBody(MagicRule namedRule, byte[] fileData, int fileLength, int baseOffset, int baseIndirectOffset, List<string> parts)
    {
        if (!_children.TryGetValue(namedRule, out var children))
        {
            return false;
        }
        bool anyMatched = false;
        foreach (var child in children)
        {
            if (child.IsDefault && anyMatched)
            {
                continue;
            }
            int childBase = child.IsContinuation ? baseOffset : 0;
            if (TryMatchTree(child, fileData, fileLength, childBase, baseOffset, parts))
            {
                anyMatched = true;
            }
        }
        return anyMatched;
    }

    /// <summary>
    /// <para>追加描述片段：非 NOSPACE 且已有内容时以单空格分隔。</para>
    /// Appends a description fragment, separated by a single space unless NOSPACE is set.
    /// </summary>
    private static void AppendPart(List<string> parts, string desc, bool noSpace)
    {
        if (string.IsNullOrEmpty(desc))
        {
            return;
        }
        if (parts.Count > 0 && !noSpace)
        {
            parts.Add(" ");
        }
        parts.Add(desc);
    }

    /// <summary>
    /// <para>顶层规则数量。</para>
    /// Number of top-level rules.
    /// </summary>
    public int Count => _topLevel.Count;

    /// <summary>
    /// <para>枚举所有顶层规则（主要用于测试与诊断）。</para>
    /// Enumerates all top-level rules (for testing/diagnostics).
    /// </summary>
    public IEnumerable<MagicRule> TopLevelRules => _topLevel;
}