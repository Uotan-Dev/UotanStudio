using UotanStudio.Firmware.SharedKernel.Magic;

namespace UotanStudio.Firmware.MagicGen;

/// <summary>
/// <para>解析中间表示，与运行时 <c>CompiledRule</c> 字段一一对应，供序列化器写出。</para>
/// Parse-time IR mirroring the runtime <c>CompiledRule</c> fields for serialization.
/// </summary>
internal sealed class RuleAst
{
    public int Indent;
    public MagicKind Kind;
    public MagicNumberType NumberType;
    public MagicFlags Flags;
    public MagicStringFlags StringFlags;
    public MagicRegexFlags RegexFlags;
    public MagicRelation Relation = MagicRelation.Equal;

    public long Offset;
    public long Value;

    public bool HasMask;
    public MagicMaskOp MaskOp;
    public long Mask;
    public bool MaskInvert;

    public int SearchRange;
    public int PStringLengthBytes = 1;
    public bool PStringBigEndian;

    public bool HasIndirectMask;
    public MagicMaskOp IndirectMaskOp = MagicMaskOp.And;
    public long IndirectMask;
    public bool IndirectMaskInvert;
    public int IndirectAddend;
    public MagicNumberType IndirectType = MagicNumberType.Lelong;
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

    public readonly List<RuleAst> Children = new();

    public int Strength = 50;

    /// <summary>解析器内部：类型 token 去掩码后的基本类型名。</summary>
    internal string TempBaseType = string.Empty;

    /// <summary>解析器内部：无法建模的值类别。</summary>
    internal SkipKind _skip;
}

/// <summary>暂不支持而需跳过的构造类别。</summary>
internal enum SkipKind
{
    None = 0,
    Range = 1,
}