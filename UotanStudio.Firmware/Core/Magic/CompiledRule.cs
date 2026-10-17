
namespace UotanStudio.Firmware.Core.Magic;

/// <summary>
/// A decoded compiled magic rule with its sub-rule tree; consumed by <see cref="CompiledMagicEngine"/>.
/// <para>解码后的单条编译型 magic 规则，含子规则树。运行时由 <see cref="CompiledMagicEngine"/> 消费。</para>
/// </summary>
internal sealed class CompiledRule
{
    public int Indent;
    public MagicKind Kind;
    public MagicNumberType NumberType;
    public MagicFlags Flags;
    public MagicStringFlags StringFlags;
    public MagicRegexFlags RegexFlags;
    public MagicRelation Relation;

    public long Offset;
    public long Value;

    public bool HasMask;
    public MagicMaskOp MaskOp;
    public long Mask;
    public bool MaskInvert;

    public int SearchRange;
    public int PStringLengthBytes;
    public bool PStringBigEndian;

    public bool HasIndirectMask;
    public MagicMaskOp IndirectMaskOp;
    public long IndirectMask;
    public bool IndirectMaskInvert;
    public int IndirectAddend;
    public MagicNumberType IndirectType;
    public long IndirectBaseOffset;

    public byte[]? Pattern;
    public string? RegexPattern;
    public string Description = string.Empty;
    public string MimeType = string.Empty;
    public string Extensions = string.Empty;
    public string Encoding = string.Empty;
    public string? Name;
    public string? UseName;
    public string Source = string.Empty;

    public readonly List<CompiledRule> Children = new();

    /// <summary>
    /// Character classes covering the [..] ranges inside string values, indexed per byte position; null means that position holds a plain byte.
    /// <para>字符串值中 [..] 区间对应的字符类别，按字节位置索引；null 表示该位置为普通字节。</para>
    /// </summary>
    public ulong[]? CharClasses;

    public System.Text.RegularExpressions.Regex? CompiledRegex;

    public int Strength = 50;

    /// <summary>
    /// Load order of the top-level rule, used to keep index-assisted matching in registration order.
    /// <para>顶层规则的加载顺序，用于让索引辅助匹配保持注册顺序。</para>
    /// </summary>
    public int TopLevelOrder;
}

/// <summary>
/// Result of a compiled-magic identification.
/// <para>编译型魔数识别结果。</para>
/// </summary>
public sealed class MagicInfo
{
    /// <summary>
    /// Human-readable description of the detected type, assembled from the matched rules.
    /// <para>由命中规则拼装而成、人类可读的类型描述。</para>
    /// </summary>
    /// <value>
    /// Sanitized non-empty description text; required on every result.
    /// <para>经过净化的非空描述文本；每个结果都必需。</para>
    /// </value>
    public required string Description { get; init; }

    /// <summary>
    /// Magic fragment that produced the match, kept for diagnostics.
    /// <para>产生该命中的 magic 片段，用于诊断。</para>
    /// </summary>
    /// <value>
    /// The originating fragment text; <see langword="null"/> when the matched rule carries no source.
    /// <para>命中来源片段文本；匹配规则未携带来源时为 <see langword="null"/>。</para>
    /// </value>
    public string? Source { get; init; }

    /// <summary>
    /// MIME type declared by the first matched rule that provides one.
    /// <para>首个提供 MIME 类型的匹配规则所声明的 MIME 类型。</para>
    /// </summary>
    /// <value>
    /// The MIME type; <see langword="null"/> when no matched rule declares one.
    /// <para>MIME 类型；没有任何匹配规则声明时为 <see langword="null"/>。</para>
    /// </value>
    public string? MimeType { get; init; }

    /// <summary>
    /// Comma-separated common file extensions declared by the first matched rule that provides them.
    /// <para>首个提供扩展名的匹配规则所声明的逗号分隔常见扩展名。</para>
    /// </summary>
    /// <value>
    /// The extension list; an entry may carry `/`-separated variants.
    /// <see langword="null"/> when no matched rule declares extensions.
    /// <para>扩展名列表；单个条目可含以 `/` 分隔的变体。没有任何匹配规则声明扩展名时为 <see langword="null"/>。</para>
    /// </value>
    public string? Extensions { get; init; }

    /// <summary>
    /// Declared encoding hint from the first matched rule that provides one,
    /// such as <c>text</c>, <c>binary</c> or a charset name.
    /// <para>首个提供编码提示的匹配规则所声明的编码提示，例如 <c>text</c>、<c>binary</c> 或字符集名称。</para>
    /// </summary>
    /// <value>
    /// The encoding hint; <see langword="null"/> when no matched rule declares one.
    /// <para>编码提示；没有任何匹配规则声明时为 <see langword="null"/>。</para>
    /// </value>
    public string? Encoding { get; init; }

    /// <summary>
    /// Whether the matched encoding hint classifies the content as text.
    /// <para>命中的编码提示是否将内容判定为文本。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when an encoding hint is present and is neither <c>binary</c> nor <c>unknown</c>.
    /// <para>存在编码提示且既不是 <c>binary</c> 也不是 <c>unknown</c> 时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsText { get; init; }

    /// <summary>
    /// Match strength of the winning candidate; higher values take priority during matching.
    /// <para>胜出候选项的匹配强度；匹配时数值越高优先级越高。</para>
    /// </summary>
    /// <value>
    /// The greatest strength among the matched top-level rules; a candidate at 100 or above ends the search early.
    /// <para>命中的顶层规则中的最大强度值；强度达到 100 及以上的候选项会提前结束搜索。</para>
    /// </value>
    public int Strength { get; init; }
}