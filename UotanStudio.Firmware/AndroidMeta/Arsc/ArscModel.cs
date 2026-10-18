using System.Buffers.Binary;
using System.Globalization;
using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.AndroidMeta.Resources;

namespace UotanStudio.Firmware.AndroidMeta.Arsc;

/// <summary>
/// Data type tag of a resource value payload, stored as the type byte of an 8-byte typed value record.
/// <para>资源值负载的数据类型标记，以 8 字节类型化值记录中的类型字节存储。</para>
/// </summary>
public enum ResValueType : byte
{
    /// <summary>Null placeholder; the data payload carries no meaning. <para>空值占位；数据负载无意义。</para></summary>
    Null = 0x00,
    /// <summary>
    /// Reference to another resource; <c>Data</c> packs package, type and entry indices.
    /// <para>对其他资源的引用；<c>Data</c> 打包了包、类型与条目索引。</para>
    /// </summary>
    Reference = 0x01,
    /// <summary>
    /// Attribute reference whose bits are laid out like <see cref="Reference"/>.
    /// <para>属性引用，位排布与 <see cref="Reference"/> 相同。</para>
    /// </summary>
    Attribute = 0x02,
    /// <summary>
    /// String value; <c>Data</c> is an index into the table's global string pool.
    /// <para>字符串值；<c>Data</c> 是表级全局字符串池的索引。</para>
    /// </summary>
    String = 0x03,
    /// <summary>
    /// IEEE-754 single-precision float held in the raw <c>Data</c> bits.
    /// <para>以 <c>Data</c> 原始位表示的 IEEE-754 单精度浮点数。</para>
    /// </summary>
    Float = 0x04,
    /// <summary>
    /// Packed complex magnitude with a unit code (px, dip, sp, pt, in, mm) in the low 4 bits.
    /// <para>紧凑复数大小，低 4 位为单位编码（px、dip、sp、pt、in、mm）。</para>
    /// </summary>
    Dimension = 0x05,
    /// <summary>
    /// Packed complex fraction rendered as a percentage; unit code 0 is <c>%</c> and 1 is <c>%p</c>.
    /// <para>以百分比呈现的紧凑复数分数；单位编码 0 为 <c>%</c>，1 为 <c>%p</c>。</para>
    /// </summary>
    Fraction = 0x06,
    /// <summary>
    /// Reference resolved against the theme at runtime; layout matches <see cref="Reference"/>.
    /// <para>运行时按主题解析的引用；位排布与 <see cref="Reference"/> 一致。</para>
    /// </summary>
    DynamicReference = 0x07,
    /// <summary>
    /// Attribute reference resolved against the theme at runtime.
    /// <para>运行时按主题解析的属性引用。</para>
    /// </summary>
    DynamicAttribute = 0x08,
    /// <summary>
    /// Signed decimal integer held in <c>Data</c>.
    /// <para>由 <c>Data</c> 表示的有符号十进制整数。</para>
    /// </summary>
    IntDec = 0x10,
    /// <summary>Raw integer formatted as hexadecimal. <para>以十六进制呈现的原始整数。</para></summary>
    IntHex = 0x11,
    /// <summary>Boolean; zero means false and any other value means true. <para>布尔值；0 为假，非 0 为真。</para></summary>
    IntBoolean = 0x12,
    /// <summary>
    /// 32-bit ARGB color rendered as eight hexadecimal digits.
    /// <para>以 8 位十六进制呈现的 32 位 ARGB 颜色。</para>
    /// </summary>
    IntColorArgb8 = 0x1c,
    /// <summary>
    /// 32-bit RGB color rendered as eight hexadecimal digits.
    /// <para>以 8 位十六进制呈现的 32 位 RGB 颜色。</para>
    /// </summary>
    IntColorRgb8 = 0x1d,
    /// <summary>
    /// 16-bit ARGB color read from the low two bytes of <c>Data</c>.
    /// <para>取自 <c>Data</c> 低两字节的 16 位 ARGB 颜色。</para>
    /// </summary>
    IntColorArgb4 = 0x1e,
    /// <summary>
    /// 16-bit RGB color read from the low two bytes of <c>Data</c>.
    /// <para>取自 <c>Data</c> 低两字节的 16 位 RGB 颜色。</para>
    /// </summary>
    IntColorRgb4 = 0x1f
}

/// <summary>
/// A typed resource value parsed from an 8-byte little-endian value record.
/// <para>从 8 字节小端类型化值记录解析出的类型化资源值。</para>
/// </summary>
public sealed class ResValue
{
    /// <summary>
    /// Value data type controlling how <see cref="Data"/> is interpreted.
    /// <para>决定 <see cref="Data"/> 解释方式的数据类型。</para>
    /// </summary>
    /// <value>
    /// The byte parsed at offset 3 of the value record; complex values keep the default
    /// <see cref="ResValueType.Null"/>.
    /// <para>从值记录偏移 3 解析的字节；复杂值保持默认的 <see cref="ResValueType.Null"/>。</para>
    /// </value>
    public ResValueType Type { get; set; }

    /// <summary>
    /// Raw 32-bit payload whose meaning is defined by <see cref="Type"/>.
    /// <para>含义由 <see cref="Type"/> 决定的原始 32 位负载。</para>
    /// </summary>
    /// <value>
    /// Little-endian bits holding a global string-pool index, a resource id, an integer, float bits
    /// or a packed complex magnitude; unused for complex values.
    /// <para>小端位，存放全局字符串池索引、资源 ID、整数、浮点位或紧凑复数大小；复杂值不使用。</para>
    /// </value>
    public uint Data { get; set; }

    /// <summary>
    /// Marks a value parsed from a complex resource entry.
    /// <para>标识从复杂资源条目解析出的值。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the payload lives in <see cref="Parent"/> and <see cref="Maps"/>
    /// rather than in <see cref="Data"/>.
    /// <para>为 <see langword="true"/> 时负载位于 <see cref="Parent"/> 与 <see cref="Maps"/>，而非 <see cref="Data"/>。</para>
    /// </value>
    public bool IsComplex { get; set; }

    /// <summary>
    /// Parent theme reference of a complex value.
    /// <para>复杂值的父主题引用。</para>
    /// </summary>
    /// <value>
    /// Resource id read from the complex entry header; 0 when the entry has no parent.
    /// <para>从复杂条目头读取的资源 ID；条目无父级时为 0。</para>
    /// </value>
    public uint Parent { get; set; }

    /// <summary>
    /// Attribute maps of a complex value, in serialized order.
    /// <para>复杂值的属性映射，按序列化顺序。</para>
    /// </summary>
    /// <value>
    /// Each map occupies 12 bytes (a uint32 name reference plus an 8-byte value); the backing list is
    /// created on first access so simple values carry no map storage, and the property is never
    /// <see langword="null"/>.
    /// <para>每个映射占 12 字节（uint32 名称引用加 8 字节值）；底层列表在首次访问时创建，因此简单值不占用映射存储，
    /// 且该属性永不为 <see langword="null"/>。</para>
    /// </value>
    public List<ResMap> Maps => _maps ??= [];

    private List<ResMap>? _maps;

    /// <summary>Formats the value as readable text (strings decoded via globalPool; references as resource IDs).
    /// <para>格式化值为可读文本（字符串值经 globalPool 解码，引用转为资源 ID 文本）。</para></summary>
    /// <param name="globalPool">Table-level string pool resolving <see cref="ResValueType.String"/> indices; when <see langword="null"/>, strings render as <c>@str:N</c> text.
    /// <para>解析 <see cref="ResValueType.String"/> 索引的表级字符串池；为 <see langword="null"/> 时字符串输出为 <c>@str:N</c> 文本。</para></param>
    /// <param name="packageId">Package byte substituted when a reference or attribute encodes 0 in its high byte.
    /// <para>引用或属性高位编码为 0 时替代使用的包字节。</para></param>
    /// <returns>Invariant-culture display text, never <see langword="null"/>; complex values render as a map-count stub.
    /// <para>固定区域设置的显示文本，永不为 <see langword="null"/>；复杂值输出映射数量摘要。</para></returns>
    /// <exception cref="OverflowException">The value is a string whose pool index exceeds <see cref="int.MaxValue"/>.
    /// <para>该值为字符串且其字符串池索引超出 <see cref="int.MaxValue"/>。</para></exception>
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

/// <summary>
/// One name/value pair inside a complex value, serialized as a uint32 attribute reference
/// followed by an 8-byte typed value.
/// <para>复杂值内部的一对名称/值，序列化为 uint32 属性引用后接 8 字节类型化值。</para>
/// </summary>
public sealed class ResMap
{
    /// <summary>Attribute reference naming this map entry. <para>为此映射命名的属性引用。</para></summary>
    /// <value>
    /// 32-bit resource id of the style attribute, stored as the uint32 preceding the nested value.
    /// <para>样式属性的 32 位资源 ID，以嵌套值前面的 uint32 存储。</para>
    /// </value>
    public uint Name { get; set; }

    /// <summary>Typed value assigned to the attribute. <para>赋给该属性的类型化值。</para></summary>
    /// <value>
    /// Nested value parsed right after <see cref="Name"/>; defaults to a fresh <see cref="ResValue"/>.
    /// <para>紧跟 <see cref="Name"/> 解析的嵌套值；默认为新建的 <see cref="ResValue"/>。</para>
    /// </value>
    public ResValue Value { get; set; } = new();
}

/// <summary>
/// A single resource entry inside a type table; absent slots in the parent list mark unused entry indices.
/// <para>类型表内的单个资源条目；父列表中缺失的槽位表示未使用的条目索引。</para>
/// </summary>
public sealed class ResEntry
{
    /// <summary>Gets or sets the entry flags. <para>获取或设置条目标志。</para></summary>
    /// <value>
    /// Raw flag bits from the entry header; bit 0 marks a complex entry, compact sparse entries keep
    /// only bits 0x2 and 0x4, and the writer preserves at most those bits.
    /// <para>条目头的原始标志位；位 0 标识复杂条目，紧凑稀疏条目仅保留 0x2 与 0x4 位，写出器最多保留这些位。</para>
    /// </value>
    public ushort Flags { get; set; }
    /// <summary>
    /// Index of the entry key name in the owning package's <see cref="ResPackage.KeyStrings"/> pool.
    /// <para>条目键名在所属包 <see cref="ResPackage.KeyStrings"/> 池中的索引。</para>
    /// </summary>
    /// <value>
    /// For compact sparse entries the parser recovers it from the 16-bit size field of the entry header.
    /// <para>紧凑稀疏条目时，解析器改从条目头的 16 位 size 字段恢复。</para>
    /// </value>
    public int KeyIndex { get; set; }

    /// <summary>The typed value this entry resolves to. <para>该条目解析到的类型化值。</para></summary>
    /// <value>
    /// <see langword="null"/> when no value is stored; the writer then serializes an all-zero typed value.
    /// <para>未存储值时为 <see langword="null"/>；此时写出器序列化为全零类型值。</para>
    /// </value>
    public ResValue? Value { get; set; }
}

/// <summary>
/// One type table chunk: all entries of a single resource type under one device configuration.
/// <para>单个类型表块：某一设备配置下单一资源类型的全部条目。</para>
/// </summary>
/// <remarks>
/// Parsed entries are stored as two parallel arrays keyed by ascending entry index, so a chunk whose
/// highest index is far above its entry count costs one index and one reference per present entry
/// instead of one list slot per index. <see cref="Entries"/> materializes the sparse list on demand
/// for callers that need slot-based access.
/// <para>解析出的条目以按索引升序排列的两个平行数组保存，因此最高索引远高于条目数的块只为每个存在的条目
/// 付出一个索引和一个引用的代价，而不是为每个索引保留一个列表槽。<see cref="Entries"/> 按需为需要槽位访问的
/// 调用方物化该稀疏列表。</para>
/// </remarks>
public sealed class ResType
{
    private const int MaxEntrySlotCount = ushort.MaxValue + 1;

    private int[] _entryIndices = [];
    private ResEntry[] _entryValues = [];
    private int _compactCount;
    private int _slotCount;
    private List<ResEntry?>? _entriesView;
    private byte[] _configBytes = [];
    private string? _configDescription;

    /// <summary>Package-local resource type identifier. <para>包内资源类型标识。</para></summary>
    /// <value>
    /// 1-based byte from the type chunk header, occupying bits 16-23 of every resource id it defines.
    /// <para>来自类型块头的 1 基字节，占其定义的每个资源 ID 的第 16-23 位。</para>
    /// </value>
    public byte Id { get; set; }

    /// <summary>
    /// Index of this type's name in the package's <see cref="ResPackage.TypeStrings"/> pool.
    /// <para>此类型名称在包内 <see cref="ResPackage.TypeStrings"/> 池中的索引。</para>
    /// </summary>
    /// <value>
    /// The parser derives it as <c>Id - 1</c> clamped at 0; the writer does not consume it.
    /// <para>解析器按 <c>Id - 1</c> 且不小于 0 推导；写出器不消费该值。</para>
    /// </value>
    public int TypeNameIndex { get; set; }

    /// <summary>Readable description of the configuration this table applies to. <para>此表所适用配置的可读描述。</para></summary>
    /// <value>
    /// Derived from the mcc, mnc, locale, orientation and density fields of the raw config when no
    /// explicit value was assigned; <c>default</c> when no qualifier is set or the config is shorter
    /// than 16 bytes, and empty for a type that has no config bytes.
    /// <para>未显式赋值时由原始配置的 mcc、mnc、语言区域、方向与密度字段推导；无限定符或配置短于 16 字节时为
    /// <c>default</c>，没有配置字节的类型则为空字符串。</para>
    /// </value>
    public string ConfigDescription
    {
        get => _configDescription ?? (_configBytes.Length == 0 ? string.Empty : ResConfig.Describe(_configBytes));
        set => _configDescription = value;
    }

    /// <summary>
    /// Upper-case hexadecimal copy of the raw little-endian configuration bytes.
    /// <para>原始小端配置字节的十六进制大写副本。</para>
    /// </summary>
    /// <value>
    /// The writer restores these bytes and rewrites the embedded size field; an empty string
    /// serializes a zero-filled default configuration.
    /// <para>写出器还原这些字节并重写内嵌长度字段；空字符串会序列化为全零的默认配置。</para>
    /// </value>
    public string ConfigHex
    {
        get => _configBytes.Length == 0 ? string.Empty : Convert.ToHexString(_configBytes);
        set => _configBytes = string.IsNullOrEmpty(value) ? [] : Convert.FromHexString(value);
    }

    /// <summary>
    /// Sparse entry slots indexed by resource entry index.
    /// <para>按资源条目索引的稀疏条目槽。</para>
    /// </summary>
    /// <value>
    /// A <see langword="null"/> slot marks an absent entry (the writer emits the 0xFFFFFFFF offset
    /// sentinel); the list covers every index up to the highest one seen, and indices must fit
    /// 16 bits. Parsing keeps entries in compact arrays and materializes this list on first access.
    /// <para><see langword="null"/> 槽表示条目缺失（写出器输出 0xFFFFFFFF 偏移哨兵值）；列表覆盖到最大索引为止的
    /// 每个索引，且索引必须能容纳于 16 位。解析时条目保存在紧凑数组中，本列表在首次访问时才物化。</para>
    /// </value>
    public List<ResEntry?> Entries => _entriesView ??= MaterializeEntries();

    internal byte[] ConfigBytes => _configBytes;

    internal int EntryCount => _entriesView?.Count ?? _slotCount;

    internal int PresentEntryCount
    {
        get
        {
            if (_entriesView is not { } view)
            {
                return _compactCount;
            }
            var count = 0;
            foreach (var entry in view)
            {
                if (entry is not null)
                {
                    count++;
                }
            }
            return count;
        }
    }

    internal List<ResEntry?>? MaterializedEntries => _entriesView;

    internal int CompactEntryCount => _compactCount;

    internal int CompactEntryIndex(int ordinal) => _entryIndices[ordinal];

    internal ResEntry CompactEntryValue(int ordinal) => _entryValues[ordinal];

    internal IEnumerable<ResEntry> EnumerateEntries()
    {
        if (_entriesView is { } view)
        {
            foreach (var entry in view)
            {
                if (entry is not null)
                {
                    yield return entry;
                }
            }
            yield break;
        }
        for (var i = 0; i < _compactCount; i++)
        {
            yield return _entryValues[i];
        }
    }

    internal void SetConfig(byte[] bytes, string description)
    {
        _configBytes = bytes;
        _configDescription = description;
    }

    internal void SetEntry(int index, ResEntry? entry)
    {
        if (_entriesView is { } view)
        {
            while (view.Count <= index)
            {
                view.Add(null);
            }
            view[index] = entry;
            return;
        }
        if (index >= _slotCount)
        {
            _slotCount = index + 1;
        }
        if (entry is null)
        {
            return;
        }
        if (_compactCount == _entryIndices.Length)
        {
            var capacity = _entryIndices.Length == 0
                ? 4
                : Math.Min(_entryIndices.Length * 2, MaxEntrySlotCount);
            Array.Resize(ref _entryIndices, capacity);
            Array.Resize(ref _entryValues, capacity);
        }
        _entryIndices[_compactCount] = index;
        _entryValues[_compactCount] = entry;
        _compactCount++;
    }

    internal ResEntry? FindEntry(int index)
    {
        if (_entriesView is { } view)
        {
            return (uint)index < (uint)view.Count ? view[index] : null;
        }
        var low = 0;
        var high = _compactCount - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var candidate = _entryIndices[middle];
            if (candidate == index)
            {
                return _entryValues[middle];
            }
            if (candidate < index)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return null;
    }

    private List<ResEntry?> MaterializeEntries()
    {
        var entries = new List<ResEntry?>(_slotCount);
        var next = 0;
        for (var i = 0; i < _compactCount; i++)
        {
            while (next < _entryIndices[i])
            {
                entries.Add(null);
                next++;
            }
            entries.Add(_entryValues[i]);
            next++;
        }
        while (entries.Count < _slotCount)
        {
            entries.Add(null);
        }
        return entries;
    }
}

/// <summary>
/// Derives the readable configuration qualifier string from raw little-endian config bytes.
/// <para>从原始小端配置字节推导可读的配置限定符字符串。</para>
/// </summary>
internal static class ResConfig
{
    internal static string Describe(ReadOnlySpan<byte> config)
    {
        if (config.Length < 16) return "default";
        var parts = new List<string>();
        var mcc = BinaryPrimitives.ReadUInt16LittleEndian(config[4..]);
        var mnc = BinaryPrimitives.ReadUInt16LittleEndian(config[6..]);
        if (mcc != 0) parts.Add("mcc" + mcc.ToString("D3", CultureInfo.InvariantCulture));
        if (mnc != 0) parts.Add("mnc" + mnc.ToString("D3", CultureInfo.InvariantCulture));
        var language = DecodeLocale(config[8], config[9], 'a');
        var region = DecodeLocale(config[10], config[11], '0');
        if (language.Length > 0) parts.Add(language);
        if (region.Length > 0) parts.Add("r" + region);
        if (config[12] == 1) parts.Add("port");
        else if (config[12] == 2) parts.Add("land");
        var density = BinaryPrimitives.ReadUInt16LittleEndian(config[14..]);
        if (density != 0) parts.Add(density switch { 0xfffe => "anydpi", 0xffff => "nodpi", _ => "d" + density });
        return parts.Count > 0 ? string.Join('-', parts) : "default";
    }

    private static string DecodeLocale(byte first, byte second, char basis)
    {
        if (first == 0) return string.Empty;
        if ((first & 0x80) == 0) return new string([(char)first, (char)second]).TrimEnd('\0');
        return new string([(char)(basis + (second & 31)),
            (char)(basis + ((second & 224) >> 5) + ((first & 3) << 3)),
            (char)(basis + ((first & 124) >> 2))]);
    }
}

/// <summary>
/// A parsed package chunk (0x200) holding the package identity, its type and key string pools,
/// type-spec flags and type tables.
/// <para>已解析的包块（类型 0x200），包含包标识、类型与键字符串池、类型规范标志以及类型表。</para>
/// </summary>
public sealed class ResPackage
{
    internal List<byte[]> ExtraChunks { get; } = [];
    /// <summary>Gets or sets the resource type ID offset. <para>获取或设置资源类型 ID 偏移。</para></summary>
    /// <value>
    /// Raw package-header field read from byte 284 when the header is at least 288 bytes and written
    /// back verbatim; 0 when the header is shorter.
    /// <para>头部至少 288 字节时从字节 284 读取并原样写回的包头原始字段；头部较短时为 0。</para>
    /// </value>
    public uint TypeIdOffset { get; set; }
    /// <summary>
    /// Package identifier byte.
    /// <para>包标识字节。</para>
    /// </summary>
    /// <value>
    /// Referenced as the top byte of resource ids inside this package; parsing fails when the header
    /// uint32 does not fit one byte.
    /// <para>作为本包内资源 ID 最高字节被引用；当头部的 uint32 超出一字节时解析失败。</para>
    /// </value>
    public byte Id { get; set; }

    /// <summary>
    /// Package name decoded from the fixed 256-byte UTF-16 header field.
    /// <para>从固定 256 字节 UTF-16 头部字段解码的包名。</para>
    /// </summary>
    /// <value>
    /// Truncated at the first NUL when parsing; writing rejects names longer than 127 UTF-16 code
    /// units or containing an embedded NUL.
    /// <para>解析时在首个 NUL 处截断；写出时拒绝超过 127 个 UTF-16 码元或内嵌 NUL 的名称。</para>
    /// </value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// String pool of type names for this package.
    /// <para>本包类型名称的字符串池。</para>
    /// </summary>
    /// <value>
    /// Parsed when a pool chunk sits at the header's typeStrings offset; never <see langword="null"/>.
    /// <para>当池块位于头部 typeStrings 偏移处时解析；永不为 <see langword="null"/>。</para>
    /// </value>
    public AXmlStringPool TypeStrings { get; } = new();

    /// <summary>String pool of entry key (attribute) names. <para>条目键（属性）名称的字符串池。</para></summary>
    /// <value>
    /// Indexed by <see cref="ResEntry.KeyIndex"/>; parsed only when located at the header's keyStrings offset.
    /// <para>由 <see cref="ResEntry.KeyIndex"/> 索引；仅当位于头部 keyStrings 偏移处时解析。</para>
    /// </value>
    public AXmlStringPool KeyStrings { get; } = new();

    /// <summary>Type table chunks in stream order. <para>按流顺序排列的类型表块。</para></summary>
    /// <value>
    /// Several items may share one <see cref="ResType.Id"/>, one per configuration; never <see langword="null"/>.
    /// <para>多个项可共用同一 <see cref="ResType.Id"/>（每种配置一个）；永不为 <see langword="null"/>。</para>
    /// </value>
    public List<ResType> Types { get; } = [];

    /// <summary>
    /// Per-entry specification flags from type-spec chunks (0x202), keyed by type id.
    /// <para>来自类型规范块（0x202）的按条目规范标志，以类型 ID 为键。</para>
    /// </summary>
    /// <value>
    /// Parsing bounds each array length by <c>(chunk size - 16) / 4</c>; types without a spec chunk
    /// are absent from the dictionary.
    /// <para>解析时数组长度上限为 <c>(块大小 - 16) / 4</c>；没有规范块的类型不会出现在字典中。</para>
    /// </value>
    public Dictionary<byte, uint[]> TypeSpecFlags { get; } = [];

    /// <summary>Number of type table chunks in this package. <para>本包中类型表块的数量。</para></summary>
    /// <value>
    /// Counts every configuration, so it may exceed the number of distinct <see cref="ResType.Id"/> values.
    /// <para>按配置逐个计数，因此可能大于不同的 <see cref="ResType.Id"/> 数量。</para>
    /// </value>
    public int TypeCount => Types.Count;
}

/// <summary>
/// Root model of a resource table: the top-level chunk (magic 2) with its global string pool and packages.
/// <para>资源表的根模型：顶层块（魔数 2）及其全局字符串池与包集合。</para>
/// </summary>
public sealed class ResourceTable
{
    internal List<byte[]> ExtraChunks { get; } = [];
    /// <summary>Table-level string pool shared by all packages. <para>所有包共享的表级字符串池。</para></summary>
    /// <value>
    /// Its entries are what <see cref="ResValueType.String"/> values index via
    /// <see cref="ResValue.Data"/>; never <see langword="null"/>.
    /// <para><see cref="ResValueType.String"/> 值正是通过 <see cref="ResValue.Data"/> 索引其中的条目；
    /// 永不为 <see langword="null"/>。</para>
    /// </value>
    public AXmlStringPool GlobalPool { get; } = new();

    /// <summary>Packages parsed in chunk order. <para>按块顺序解析出的包。</para></summary>
    /// <value>
    /// Parsing fails unless the count matches the uint32 package-count field of the table header.
    /// <para>数量与表头的 uint32 包计数字段不一致时解析失败。</para>
    /// </value>
    public List<ResPackage> Packages { get; } = [];

    /// <summary>
    /// Produces a readable description summary (package/type/entry statistics).
    /// <para>生成可读描述摘要（包/类型/条目统计）。</para>
    /// </summary>
    /// <returns>
    /// Plain text with one header line (package and global-string counts) plus one block per package
    /// listing name, distinct type ids, type tables and non-null entries.
    /// <para>纯文本：一行表头（包数与全局字符串数），随后每包一段，列出名称、去重类型 ID 数、类型表数与非空条目数。</para>
    /// </returns>
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
            var entryCount = pkg.Types.Sum(t => t.PresentEntryCount);
            var typeCount = pkg.Types.Select(t => (int)t.Id).Distinct().Count();
            sb.Append(" | types: ").Append(typeCount).Append(" | tables: ").Append(pkg.Types.Count);
            sb.Append(" | entries: ").Append(entryCount);
        }
        return sb.ToString();
    }
}