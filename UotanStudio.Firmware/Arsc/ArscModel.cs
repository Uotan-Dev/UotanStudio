using System.Globalization;
using UotanStudio.Firmware.AXml;

namespace UotanStudio.Firmware.Arsc;

/// <summary>
/// <para>ARSC 条目值类型（对应 AOSP <c>Res_value</c> 的 <c>dataType</c>，与 AXML 一致）。</para>
/// ARSC entry value types (matching AOSP <c>Res_value.dataType</c>; identical to AXML).
/// </summary>
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

/// <summary>
/// <para>资源条目值：类型 + 数据 + 可选的字符串索引与复杂条目映射。</para>
/// Resource entry value: type + data, plus an optional string index and complex-entry maps.
/// </summary>
public sealed class ResValue
{
    /// <summary>值类型。<para>Value type.</para></summary>
    public ResValueType Type { get; set; }

    /// <summary>原始数据字段。<para>Raw data field.</para></summary>
    public uint Data { get; set; }

    /// <summary>
    /// <para>复杂条目标志（FLAG_COMPLEX）：条目后跟 <see cref="Maps"/> 而非简单值。</para>
    /// Complex-entry flag (FLAG_COMPLEX): the entry carries <see cref="Maps"/> instead of a simple value.
    /// </summary>
    public bool IsComplex { get; set; }

    /// <summary>复杂条目的父资源 ID。<para>Parent resource ID of a complex entry.</para></summary>
    public uint Parent { get; set; }

    /// <summary>复杂条目的 name→value 映射列表。<para>Complex-entry name→value map list.</para></summary>
    public List<ResMap> Maps { get; } = [];

    /// <summary>
    /// <para>格式化值为可读文本（字符串值经 keyStrings 解码，引用转为资源 ID 文本）。</para>
    /// Formats the value as readable text (string values decoded via keyStrings; references as resource IDs).
    /// </summary>
    /// <param name="globalPool">全局字符串池（String 类型解码）。<para>The global string pool (String-typed values).</para></param>
    /// <param name="packageId">所属包 ID（引用格式化）。<para>The owning package ID (for reference formatting).</para></param>
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
        var raw = unchecked((int)Data);
        var low4 = Data & 0x0f;
        var value = raw / (float)0x10000; // 高位为 mantissa，低位 4 位为单位
        var unit = low4 switch
        {
            0 => "px", 1 => "dip", 2 => "sp", 3 => "pt", 4 => "in", 5 => "mm", _ => string.Empty
        };
        return value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }

    private string FormatFraction()
    {
        var value = unchecked((int)Data) / 65536f;
        var unit = (Data & 0x0f) switch { 0 => "%", 1 => "%p", _ => string.Empty };
        return value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }
}

/// <summary>
/// <para>复杂条目映射（ResTable_map）：名称资源 ID → 值。</para>
/// Complex-entry map (ResTable_map): name resource ID → value.
/// </summary>
public sealed class ResMap
{
    /// <summary>映射名称（资源 ID，通常为 attrs 类型）。<para>Map name (a resource ID, usually of the attrs type).</para></summary>
    public uint Name { get; set; }

    /// <summary>映射值。<para>The mapped value.</para></summary>
    public ResValue Value { get; set; } = new();
}

/// <summary>
/// <para>资源条目：键名（keyStrings 索引）+ 值。</para>
/// Resource entry: key name (keyStrings index) + value.
/// </summary>
public sealed class ResEntry
{
    /// <summary>键名（keyStrings 索引）。<para>Key name (keyStrings index).</para></summary>
    public int KeyIndex { get; set; }

    /// <summary>条目值；NO_ENTRY 为 <c>null</c>。<para>The entry value; <c>null</c> for NO_ENTRY.</para></summary>
    public ResValue? Value { get; set; }
}

/// <summary>
/// <para>资源类型表（ResTable_type）：某类型（如 string/layout）在指定配置下的条目集合。</para>
/// Resource type table (ResTable_type): entries of one type (e.g. string/layout) for a given configuration.
/// </summary>
public sealed class ResType
{
    /// <summary>类型 ID（从 1 开始）。<para>Type ID (starts at 1).</para></summary>
    public byte Id { get; set; }

    /// <summary>类型名称（typeStrings 索引）。<para>Type name (typeStrings index).</para></summary>
    public int TypeNameIndex { get; set; }

    /// <summary>配置描述（locale/density 等，可读文本）。<para>Configuration description (locale/density etc.).</para></summary>
    public string ConfigDescription { get; set; } = string.Empty;

    /// <summary>配置原始字节（十六进制展示）。<para>Raw configuration bytes (hex display).</para></summary>
    public string ConfigHex { get; set; } = string.Empty;

    /// <summary>条目列表（按 entry index 顺序；缺失项为 null）。<para>Entries by index; missing slots are null.</para></summary>
    public List<ResEntry?> Entries { get; } = [];
}

/// <summary>
/// <para>资源包（ResTable_package）：包 ID、包名、类型字符串池与各类型表。</para>
/// Resource package (ResTable_package): package ID, name, type string pool and type tables.
/// </summary>
public sealed class ResPackage
{
    /// <summary>包 ID（0x7f 等）。<para>Package ID (e.g. 0x7f).</para></summary>
    public byte Id { get; set; }

    /// <summary>包名。<para>Package name.</para></summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>类型字符串池（string/layout/drawable…）。<para>Type string pool.</para></summary>
    public AXmlStringPool TypeStrings { get; } = new();

    /// <summary>键名字符串池。<para>Key string pool.</para></summary>
    public AXmlStringPool KeyStrings { get; } = new();

    /// <summary>类型表（按文件顺序；含 ResTableTypeSpec 与 ResTableType）。<para>Type tables (in file order).</para></summary>
    public List<ResType> Types { get; } = [];

    /// <summary>
    /// <para>类型规范标志（typeId → ResTableTypeSpec 的 flags 数组，重建时原样保留）。</para>
    /// Type-spec flags (typeId → ResTableTypeSpec flags array, preserved verbatim on rebuild).
    /// </summary>
    public Dictionary<byte, uint[]> TypeSpecFlags { get; } = [];

    /// <summary>按类型 ID 与配置分组的类型表计数（统计展示用）。<para>Type table counts grouped by ID (for stats).</para></summary>
    public int TypeCount => Types.Count;
}

/// <summary>
/// <para>解析后的 ARSC 资源表：全局字符串池 + 包列表。</para>
/// Parsed ARSC resource table: global string pool + package list.
/// </summary>
public sealed class ResourceTable
{
    /// <summary>全局字符串池（值字符串等）。<para>Global string pool (value strings etc.).</para></summary>
    public AXmlStringPool GlobalPool { get; } = new();

    /// <summary>资源包列表。<para>Package list.</para></summary>
    public List<ResPackage> Packages { get; } = [];

    /// <summary>
    /// <para>生成可读描述摘要（包/类型/条目统计）。</para>
    /// Produces a readable description summary (package/type/entry statistics).
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
