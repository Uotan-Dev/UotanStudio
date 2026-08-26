using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.Common;

/// <summary>
/// <para>文件类型匹配结果，包含类型描述与匹配规则的来源信息。</para>
/// File-type match result, carrying the type description and the source of the matched rule.
/// </summary>
public readonly struct MagicMatch : IEquatable<MagicMatch>
{
    /// <summary>
    /// <para>类型描述文本（如 "ELF 64-bit AArch64 executable"）。</para>
    /// Human-readable type description (e.g. "ELF 64-bit AArch64 executable").
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// <para>匹配规则集的来源标识（"builtin"、"embedded"、"external:&lt;path&gt;"）。</para>
    /// Source identifier of the matched rule set ("builtin", "embedded", "external:&lt;path&gt;").
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// <para>是否匹配成功。规则集中的任何顶层规则命中即为匹配，描述文本可能为空（由不含
    /// 描述模板的规则触发）。</para>
    /// Whether the match succeeded. Any top-level rule hit in a rule set counts as a match, even if the
    /// description text is empty (triggered by rules without a description template).
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
/// <para>比较运算符类型。对应上游 file(1) 的 <c>=</c>、<c>!</c>、<c>&lt;</c>、<c>&gt;</c>、<c>x</c>（任意）等。</para>
/// Comparison operator type, corresponding to file(1)'s <c>=</c>, <c>!</c>, <c>&lt;</c>, <c>&gt;</c>, <c>x</c>.
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
    /// <summary>按位与（test value 为 mask，匹配 (actual &amp; mask) != 0）</summary>
    BitAnd,
}

/// <summary>
/// <para>匹配值类型。对应上游 file(1) 的 <c>byte</c>/<c>short</c>/<c>long</c>/<c>string</c>/<c>regex</c> 等。</para>
/// Match value type, corresponding to file(1)'s <c>byte</c>/<c>short</c>/<c>long</c>/<c>string</c>/<c>regex</c>.
/// </summary>
public enum MagicValueType
{
    /// <summary>无类型（仅检查存在性）</summary>
    None,
    /// <summary>ASCII/UTF-8 字符串（支持 \xNN 转义）</summary>
    String,
    /// <summary>字节序敏感的整数：byte/short/long/quad/leid/leshort 等</summary>
    Integer,
    /// <summary>正则匹配（简化子集，不实现完整 POSIX）</summary>
    Regex,
}

/// <summary>
/// <para>单条 magic 规则的匹配条件与描述模板。</para>
/// Match condition and description template for a single magic rule.
/// </summary>
public sealed class MagicRule
{
    public int Offset { get; }
    public bool OffsetFromEnd { get; }
    public MagicValueType ValueType { get; }
    public int IntegerWidth { get; }
    public bool BigEndian { get; }
    public MagicCompareOp CompareOp { get; }
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

    public string DescriptionTemplate { get; }
    public int Indent { get; }
    public bool IsInverse { get; }
    public bool IsIndirect { get; }
    public int IndirectBaseWidth { get; }
    public bool IndirectBigEndian { get; }
    public int IndirectAddend { get; }

    internal MagicRule(
        int offset, bool offsetFromEnd,
        MagicValueType valueType, int integerWidth, bool bigEndian,
        MagicCompareOp compareOp, byte[] pattern,
        string descriptionTemplate, int indent, bool isInverse,
        bool isIndirect, int indirectBaseWidth, bool indirectBigEndian, int indirectAddend,
        long mask = 0, bool hasMask = false)
    {
        Offset = offset;
        OffsetFromEnd = offsetFromEnd;
        ValueType = valueType;
        IntegerWidth = integerWidth;
        BigEndian = bigEndian;
        CompareOp = compareOp;
        Pattern = pattern;
        DescriptionTemplate = descriptionTemplate;
        Indent = indent;
        IsInverse = isInverse;
        IsIndirect = isIndirect;
        IndirectBaseWidth = indirectBaseWidth;
        IndirectBigEndian = indirectBigEndian;
        IndirectAddend = indirectAddend;
        Mask = mask;
        HasMask = hasMask;
    }

    /// <summary>
    /// <para>尝试在给定文件字节范围上匹配本规则。</para>
    /// Attempts to match this rule against the provided file data.
    /// </summary>
    public string? TryMatch(byte[] fileData, int fileLength)
    {
        if (IsIndirect)
        {
            return TryMatchIndirect(fileData, fileLength);
        }

        int absoluteOffset = OffsetFromEnd ? (fileLength + Offset) : Offset;
        if (absoluteOffset < 0 || absoluteOffset > fileData.Length)
        {
            return null;
        }

        return TryMatchAt(fileData, absoluteOffset, fileLength);
    }

    private string? TryMatchAt(byte[] fileData, int offset, int fileLength)
    {
        if (CompareOp == MagicCompareOp.Any)
        {
            if (offset + 1 <= fileData.Length)
            {
                return FormatDescription(fileData, offset, fileLength, intValue: null);
            }
            return null;
        }

        if (ValueType == MagicValueType.String)
        {
            if (!TryMatchBytes(fileData, offset, Pattern, out _))
            {
                return null;
            }
            return FormatDescription(fileData, offset, fileLength, intValue: null);
        }

        if (ValueType == MagicValueType.Integer)
        {
            if (offset + IntegerWidth > fileData.Length)
            {
                return null;
            }
            long actual = ReadInteger(fileData, offset, IntegerWidth, BigEndian);

            // 应用 type&mask
            if (HasMask)
            {
                actual &= Mask;
            }

            long expected = ReadInteger(Pattern, 0, Pattern.Length, bigEndian: true);

            if (CompareOp == MagicCompareOp.BitAnd)
            {
                // test value 为 mask，匹配 (actual & mask) != 0
                if ((actual & expected) == 0)
                {
                    return null;
                }
            }
            else if (!CompareValues(actual, expected))
            {
                return null;
            }
            return FormatDescription(fileData, offset, fileLength, intValue: actual);
        }

        return null;
    }

    private string? TryMatchIndirect(byte[] fileData, int fileLength)
    {
        int baseOffset = OffsetFromEnd ? (fileLength + Offset) : Offset;
        if (baseOffset < 0 || baseOffset + IndirectBaseWidth > fileData.Length)
        {
            return null;
        }
        long indirectOffset = ReadInteger(fileData, baseOffset, IndirectBaseWidth, IndirectBigEndian);
        int actualOffset = (int)(indirectOffset + IndirectAddend);
        if (actualOffset < 0 || actualOffset > fileData.Length)
        {
            return null;
        }
        return TryMatchAt(fileData, actualOffset, fileLength);
    }

    private bool TryMatchBytes(byte[] data, int offset, byte[] pattern, out int consumed)
    {
        consumed = 0;
        if (offset + pattern.Length > data.Length)
        {
            return false;
        }
        for (int i = 0; i < pattern.Length; i++)
        {
            if (data[offset + i] != pattern[i])
            {
                return false;
            }
        }
        consumed = pattern.Length;
        return true;
    }

    private static long ReadInteger(byte[] data, int offset, int width, bool bigEndian)
    {
        return width switch
        {
            1 => data[offset],
            2 => bigEndian ? (data[offset] << 8) | data[offset + 1]
                           : (data[offset + 1] << 8) | data[offset],
            4 => bigEndian ? ((long)data[offset] << 24) | ((long)data[offset + 1] << 16)
                                   | ((long)data[offset + 2] << 8) | data[offset + 3]
                           : ((long)data[offset + 3] << 24) | ((long)data[offset + 2] << 16)
                                   | ((long)data[offset + 1] << 8) | data[offset],
            8 => ReadInt64(data, offset, bigEndian),
            _ => 0
        };
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

    private bool CompareValues(long actual, long expected)
    {
        return CompareOp switch
        {
            MagicCompareOp.Equal => actual == expected,
            MagicCompareOp.NotEqual => actual != expected,
            MagicCompareOp.LessThan => actual < expected,
            MagicCompareOp.GreaterThan => actual > expected,
            _ => actual == expected
        };
    }

    private string FormatDescription(byte[] data, int offset, int fileLength, long? intValue)
    {
        if (string.IsNullOrEmpty(DescriptionTemplate))
        {
            return ValueType == MagicValueType.Integer && intValue.HasValue
                ? intValue.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }
        return SubstitutePlaceholders(DescriptionTemplate, data, offset, fileLength, intValue);
    }

    private static string SubstitutePlaceholders(
        string template, byte[] data, int offset, int fileLength, long? intValue)
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
            string segment = spec switch
            {
                's' => intValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                'd' or 'i' => intValue?.ToString(CultureInfo.InvariantCulture) ?? "0",
                'x' => (intValue ?? 0).ToString("x", CultureInfo.InvariantCulture),
                'X' => (intValue ?? 0).ToString("X", CultureInfo.InvariantCulture),
                'c' => intValue.HasValue && intValue.Value >= 0 && intValue.Value <= 0xFF
                    ? ((char)intValue.Value).ToString()
                    : "?",
                '%' => "%",
                _ => "%" + spec
            };

            if (width > 0 && spec != '%')
            {
                segment = leftAlign ? segment.PadRight(width) : segment.PadLeft(width);
            }

            sb.Append(segment);
            i = j + 1;
        }
        return sb.ToString();
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
    }

    /// <summary>
    /// <para>对文件数据执行匹配，返回首个命中的顶层规则及其子规则拼接的描述。</para>
    /// Matches the file data, returning the first top-level rule hit and the description concatenated with
    /// its sub-rule matches.
    /// </summary>
    public MagicMatch Match(byte[] fileData, int fileLength)
    {
        foreach (var rule in _topLevel)
        {
            string? desc = rule.TryMatch(fileData, fileLength);
            if (desc is null)
            {
                continue;
            }

            // 收集子规则匹配结果，追加到描述后
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(desc))
            {
                parts.Add(desc);
            }

            if (_children.TryGetValue(rule, out var children))
            {
                foreach (var child in children)
                {
                    string? childDesc = child.TryMatch(fileData, fileLength);
                    if (!string.IsNullOrEmpty(childDesc))
                    {
                        parts.Add(childDesc);
                    }
                }
            }

            // 顶层规则命中即视为匹配（与上游 file_softmagic 一致），即便未产生任何描述文本。
            // A hit on a top-level rule counts as a match (like upstream file_softmagic), even if no
            // description text was produced.
            return new MagicMatch(string.Join(" ", parts), _source, isMatch: true);
        }
        return MagicMatch.None;
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
