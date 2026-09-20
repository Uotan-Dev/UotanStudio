
namespace UotanStudio.Firmware.SharedKernel.Magic;

/// <summary>
/// <para>解码后的单条编译型 magic 规则，含子规则树。运行时由 <see cref="CompiledMagicEngine"/> 消费。</para>
/// A decoded compiled magic rule with its sub-rule tree; consumed by <see cref="CompiledMagicEngine"/>.
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

    /// <summary>字符类别（string 值中的 [..] 区间），逐字节位置；null 表示该位为普通字节。</summary>
    public ulong[]? CharClasses;

    public System.Text.RegularExpressions.Regex? CompiledRegex;

    public int Strength = 50;
}

/// <summary>
/// <para>编译型魔数识别结果。</para>
/// Result of a compiled-magic identification.
/// </summary>
public sealed class MagicInfo
{
    /// <summary>人类可读类型描述。 / Human-readable type description.</summary>
    public required string Description { get; init; }

    /// <summary>命中来源文件（诊断用）。 / Originating fragment (diagnostics).</summary>
    public string? Source { get; init; }

    /// <summary>MIME 类型（若有）。 / MIME type when known.</summary>
    public string? MimeType { get; init; }

    /// <summary>常见扩展名（逗号分隔，可含 `/` 变体，若有）。 / Common extensions (comma-separated) when known.</summary>
    public string? Extensions { get; init; }

    /// <summary>文本编码（`text`/`binary`/charset 提示），若有。 / Declared encoding hint when known.</summary>
    public string? Encoding { get; init; }

    /// <summary>是否被判为文本。 / Whether classified as text.</summary>
    public bool IsText { get; init; }

    /// <summary>匹配强度（越大越优先）。 / Match strength (higher wins).</summary>
    public int Strength { get; init; }
}