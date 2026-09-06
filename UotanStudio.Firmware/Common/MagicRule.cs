using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.Common;

public readonly struct MagicMatch : IEquatable<MagicMatch>
{
    public string Description { get; }

    public string Source { get; }

    public bool IsMatch { get; }

    /// <summary>
    /// Creates a match whose IsMatch derives from a non-empty description.
    /// <para>以描述文本推导匹配状态创建匹配结果。</para>
    /// </summary>
    public MagicMatch(string description, string source)
        : this(description, source, !string.IsNullOrEmpty(description))
    {
    }

    /// <summary>
    /// Creates a match with an explicit IsMatch (for top-level rule hits without text).
    /// <para>以显式匹配状态创建匹配结果（用于顶层规则命中但无描述文本的场景）。</para>
    /// </summary>
    public MagicMatch(string description, string source, bool isMatch)
    {
        Description = description;
        Source = source;
        IsMatch = isMatch;
    }

    /// <summary>
    /// Determines whether the values are equal.
    /// <para>判断值是否相等。</para>
    /// </summary>
    public bool Equals(MagicMatch other) => Description == other.Description && Source == other.Source;

    /// <summary>
    /// Determines whether the values are equal.
    /// <para>判断值是否相等。</para>
    /// </summary>
    public override bool Equals(object? obj) => obj is MagicMatch other && Equals(other);

    /// <summary>
    /// Returns the hash code for this value.
    /// <para>返回此值的哈希码。</para>
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(Description, Source);

    public static MagicMatch None => new(string.Empty, string.Empty, false);

    /// <summary>
    /// Implicit conversion from string for reuse in the hardcoded fast-path.
    /// <para>从字符串隐式转换，便于在硬编码快速路径中复用。</para>
    /// </summary>
    public static implicit operator MagicMatch(string? description) =>
        string.IsNullOrEmpty(description) ? None : new MagicMatch(description, "builtin", true);
}

public enum MagicCompareOp
{
    Equal,
    NotEqual,
    LessThan,
    GreaterThan,
    Any,
    BitAnd,
    BitXor,
}

public enum MagicMaskOp
{
    None,
    And,
    Or,
    Xor,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
}

[Flags]
public enum MagicStringFlags
{
    None = 0,
    IgnoreCase = 1,
    CompactWhitespace = 2,
    OptionalWhitespace = 4,
    Trim = 8,
    FullWord = 16,
}

public enum MagicValueType
{
    None,
    String,
    Integer,
    Regex,
    Search,
    PString,
}

public sealed class MagicRule
{
    public int Offset { get; }

    public bool OffsetFromEnd { get; }

    public bool IsContinuation { get; }

    public MagicValueType ValueType { get; }

    public int IntegerWidth { get; }

    public bool BigEndian { get; }

    public bool IsUnsigned { get; }

    public bool IsDate { get; }

    public MagicCompareOp CompareOp { get; }

    public byte[] Pattern { get; }

    public long Mask { get; }

    public bool HasMask { get; }

    public MagicMaskOp MaskOp { get; }

    public bool MaskInverse { get; }

    public MagicStringFlags StringFlags { get; }

    public int SearchRange { get; }

    public int PStringLengthBytes { get; }

    public bool PStringBigEndian { get; }

    public string? RegexPattern { get; }

    public string DescriptionTemplate { get; }

    public int Indent { get; }

    public bool IsDefault { get; }

    public string? Name { get; }

    public string? UseName { get; }

    public bool NoSpace { get; }

    public bool IsIndirect { get; }

    public int IndirectBaseWidth { get; }

    public bool IndirectBigEndian { get; }

    public int IndirectAddend { get; }

    public long IndirectMask { get; }

    public bool IndirectHasMask { get; }

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
    /// Attempts to match this rule against the provided file data (top-level entry, no continuation base offset).
    /// <para>尝试在给定文件字节范围上匹配本规则（顶层入口，无续接基准偏移）。</para>
    /// </summary>
    public string? TryMatch(byte[] fileData, int fileLength)
    {
        return TryMatchAtOffset(fileData, fileLength, 0, 0, out _);
    }

    /// <summary>
    /// Attempts to match with a base offset: continuationBase is the end of the previous level's match, used only when IsContinuation is set.
    /// <para>以指定基准偏移尝试匹配：continuationBase 为上一层规则匹配结束位置， 仅当 IsContinuation 时参与计算。</para>
    /// </summary>
    internal string? TryMatchAtOffset(byte[] fileData, int fileLength, int continuationBase, int baseIndirectOffset, out int matchEnd)
    {
        matchEnd = 0;
        int offset;

        if (IsIndirect)
        {

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


        if (IsDefault)
        {
            return FormatDescription(fileData, offset, fileLength, intValue: null, strValue: null);
        }

        if (CompareOp == MagicCompareOp.Any)
        {

            if (offset >= fileData.Length)
            {
                return null;
            }

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

                return TryMatchPString(fileData, offset, fileLength, out matchEnd);
            }

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

                while (j < data.Length && IsSpace(data[offset + j]))
                {
                    j++;
                }
                i++;
                continue;
            }

            byte cmp = d;


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


        if (CompareOp == MagicCompareOp.NotEqual)
        {
            return null;
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
    /// String comparison for &lt;/&gt; relations (upstream file_strncmp semantics).
    /// <para>字符串比较：用于 &lt;/&gt; 等关系运算符（上游 file_strncmp 语义：返回首个差异处比较值）。</para>
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


    private string? TryMatchPString(byte[] data, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset;
        int lenBytes = PStringLengthBytes;
        if (lenBytes is not (1 or 2 or 4))
        {

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


    private string? TryMatchRegex(byte[] data, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset;
        if (string.IsNullOrEmpty(RegexPattern))
        {
            return null;
        }
        Regex rx = _compiledRegex ??= CompileRegex();

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


    private string? TryMatchInteger(byte[] data, int offset, int fileLength, out int matchEnd)
    {
        matchEnd = offset + IntegerWidth;
        if (offset + IntegerWidth > data.Length)
        {
            return null;
        }
        long actual = ReadInteger(data, offset, IntegerWidth, BigEndian);


        if (HasMask)
        {
            actual = ApplyMaskOp(actual, MaskOp, Mask);
        }
        if (MaskInverse)
        {

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
    /// Builds an all-ones mask for the field width (byte→0xff, short→0xffff, long→0xffffffff, quad→full 64-bit).
    /// <para>按字段宽度生成全 1 掩码（byte→0xff、short→0xffff、long→0xffffffff、quad→全 64 位）。</para>
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
    /// Formats a 32/64-bit epoch value as a date string (upstream file_fmttime, UTC).
    /// <para>将 32/64 位秒级时间戳格式化为日期字符串（对应上游 file_fmttime 的 YYYY-MM-DD HH:MM:SS 形式，UTC）。</para>
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
    /// Gets the matched string value (trimmed) for %s substitution.
    /// <para>取匹配到的字符串值（去除尾部空白），用于 %s 占位符输出。</para>
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


        if (rule.Name is not null)
        {
            _named[rule.Name] = rule;
        }
    }

    /// <summary>
    /// Matches the file data, returning the first top-level rule hit with its recursively matched sub-rule descriptions concatenated.
    /// <para>对文件数据执行匹配，返回首个命中的顶层规则及其递归子规则拼接的描述。</para>
    /// </summary>
    public MagicMatch Match(byte[] fileData, int fileLength)
    {
        foreach (var rule in _topLevel)
        {
            var parts = new List<string>();
            if (TryMatchTree(rule, fileData, fileLength, 0, 0, parts))
            {


                return new MagicMatch(string.Join(string.Empty, parts), _source, isMatch: true);
            }
        }
        return MagicMatch.None;
    }

    /// <summary>
    /// Recursively matches one rule and its sub-tree: on hit appends the description (honoring NOSPACE), then tries all children using the match end as continuation base; default children only apply when no previous sibling at the same level matched.
    /// <para>递归匹配单条规则及其子树：命中后追加描述（遵守 NOSPACE），再以匹配结束位置为 基准尝试全部子规则；default 子规则仅当同层此前无命中时才参与。</para>
    /// </summary>
    private bool TryMatchTree(MagicRule rule, byte[] fileData, int fileLength, int continuationBase, int baseIndirectOffset, List<string> parts)
    {

        if (rule.Name is not null)
        {
            return TryMatchNamedBody(rule, fileData, fileLength, continuationBase, baseIndirectOffset, parts);
        }


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
    /// Matches the sub-rule body of a named rule (name directive): any child hit counts as a hit and its descriptions are appended.
    /// <para>匹配命名规则（name 指令）的子规则体：任一子规则命中即视为整体命中，描述拼接到输出。</para>
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
    /// Appends a description fragment, separated by a single space unless NOSPACE is set.
    /// <para>追加描述片段：非 NOSPACE 且已有内容时以单空格分隔。</para>
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

    public int Count => _topLevel.Count;

    public IEnumerable<MagicRule> TopLevelRules => _topLevel;
}
