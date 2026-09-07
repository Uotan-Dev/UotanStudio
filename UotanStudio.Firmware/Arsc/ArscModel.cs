using System.Globalization;
using UotanStudio.Firmware.AXml;

namespace UotanStudio.Firmware.Arsc;

public enum ResValueType : byte
{
    Null = 0x00,
    Reference = 0x01,
    Attribute = 0x02,
    String = 0x03,
    Float = 0x04,
    Dimension = 0x05,
    Fraction = 0x06,
    DynamicReference = 0x07,
    DynamicAttribute = 0x08,
    IntDec = 0x10,
    IntHex = 0x11,
    IntBoolean = 0x12,
    IntColorArgb8 = 0x1c,
    IntColorRgb8 = 0x1d,
    IntColorArgb4 = 0x1e,
    IntColorRgb4 = 0x1f
}

public sealed class ResValue
{
    public ResValueType Type { get; set; }

    public uint Data { get; set; }

    public bool IsComplex { get; set; }

    public uint Parent { get; set; }

    public List<ResMap> Maps { get; } = [];

    /// <summary>
    /// Formats the value as readable text (strings decoded via globalPool; references as resource IDs).
    /// <para>格式化值为可读文本（字符串值经 globalPool 解码，引用转为资源 ID 文本）。</para>
    /// </summary>
    public string FormatValue(AXmlStringPool? globalPool, byte packageId)
    {
        if (IsComplex)
        {
            return $"<complex: {Maps.Count} maps>";
        }

        switch (Type)
        {
            case ResValueType.String:
                return globalPool?.GetString(checked((int)Data)) ?? $"@str:{Data}";
            case ResValueType.Reference:
            case ResValueType.DynamicReference:
                return FormatRef(Data, packageId);
            case ResValueType.Attribute:
            case ResValueType.DynamicAttribute:
                return FormatAttr(Data, packageId);
            case ResValueType.IntDec:
                return unchecked((int)Data).ToString(CultureInfo.InvariantCulture);
            case ResValueType.IntHex:
                return "0x" + Data.ToString("x8", CultureInfo.InvariantCulture);
            case ResValueType.IntBoolean:
                return Data != 0 ? "true" : "false";
            case ResValueType.Float:
                {
                    var f = BitConverter.Int32BitsToSingle(unchecked((int)Data));
                    return f.ToString("R", CultureInfo.InvariantCulture);
                }
            case ResValueType.Dimension:
                return FormatDimension();
            case ResValueType.Fraction:
                return FormatFraction();
            case ResValueType.IntColorArgb8:
            case ResValueType.IntColorRgb8:
                return "#" + Data.ToString("x8", CultureInfo.InvariantCulture);
            case ResValueType.IntColorArgb4:
            case ResValueType.IntColorRgb4:
                return "#" + (Data & 0xffff).ToString("x4", CultureInfo.InvariantCulture);
            case ResValueType.Null:
                return "null";
            default:
                return "0x" + Data.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    private string FormatRef(uint data, byte packageId)
    {
        if (data == 0) return "@null";
        var pkg = (byte)((data >> 24) & 0xff);
        var type = (data >> 16) & 0xff;
        var entry = data & 0xffff;
        return $"@{(pkg == 0 ? packageId : pkg):x}/0x{type:x}/0x{entry:x4}";
    }

    private string FormatAttr(uint data, byte packageId)
    {
        var pkg = (byte)((data >> 24) & 0xff);
        var type = (data >> 16) & 0xff;
        var entry = data & 0xffff;
        return $"?{(pkg == 0 ? packageId : pkg):x}/0x{type:x}/0x{entry:x4}";
    }

    private string FormatDimension()
    {
        var low4 = Data & 0x0f;
        var value = AndroidComplexValue.ToFloat(Data);
        var unit = low4 switch
        {
            0 => "px",
            1 => "dip",
            2 => "sp",
            3 => "pt",
            4 => "in",
            5 => "mm",
            _ => string.Empty
        };
        return value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }

    private string FormatFraction()
    {
        var value = AndroidComplexValue.ToFloat(Data) * 100f;
        var unit = (Data & 0x0f) switch { 0 => "%", 1 => "%p", _ => string.Empty };
        return value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }
}

public sealed class ResMap
{
    public uint Name { get; set; }

    public ResValue Value { get; set; } = new();
}

public sealed class ResEntry
{
    /// <summary>Gets or sets the entry flags. <para>获取或设置条目标志。</para></summary>
    public ushort Flags { get; set; }
    public int KeyIndex { get; set; }

    public ResValue? Value { get; set; }
}

public sealed class ResType
{
    public byte Id { get; set; }

    public int TypeNameIndex { get; set; }

    public string ConfigDescription { get; set; } = string.Empty;

    public string ConfigHex { get; set; } = string.Empty;

    public List<ResEntry?> Entries { get; } = [];
}

public sealed class ResPackage
{
    internal List<byte[]> ExtraChunks { get; } = [];
    /// <summary>Gets or sets the resource type ID offset. <para>获取或设置资源类型 ID 偏移。</para></summary>
    public uint TypeIdOffset { get; set; }
    public byte Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public AXmlStringPool TypeStrings { get; } = new();

    public AXmlStringPool KeyStrings { get; } = new();

    public List<ResType> Types { get; } = [];

    public Dictionary<byte, uint[]> TypeSpecFlags { get; } = [];

    public int TypeCount => Types.Count;
}

public sealed class ResourceTable
{
    internal List<byte[]> ExtraChunks { get; } = [];
    public AXmlStringPool GlobalPool { get; } = new();

    public List<ResPackage> Packages { get; } = [];

    /// <summary>
    /// Produces a readable description summary (package/type/entry statistics).
    /// <para>生成可读描述摘要（包/类型/条目统计）。</para>
    /// </summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Android Resource Table (ARSC)");
        sb.Append(" | packages: ").Append(Packages.Count);
        sb.Append(" | global strings: ").Append(GlobalPool.Count);
        foreach (var pkg in Packages)
        {
            sb.AppendLine();
            sb.Append("  package 0x").Append(pkg.Id.ToString("x2")).Append(' ').Append(pkg.Name);
            var entryCount = pkg.Types.Sum(t => t.Entries.Count(e => e is not null));
            var typeCount = pkg.Types.Select(t => (int)t.Id).Distinct().Count();
            sb.Append(" | types: ").Append(typeCount).Append(" | tables: ").Append(pkg.Types.Count);
            sb.Append(" | entries: ").Append(entryCount);
        }
        return sb.ToString();
    }
}