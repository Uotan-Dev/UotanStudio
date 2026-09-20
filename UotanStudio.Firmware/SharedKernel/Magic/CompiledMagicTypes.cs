namespace UotanStudio.Firmware.SharedKernel.Magic;

/// <summary>
/// <para>编译型 magic 库的规则负载类别。</para>
/// Payload class of a compiled magic rule.
/// </summary>
public enum MagicKind : byte
{
    /// <summary>无负载（default / use / name 等）。 / No value payload (default / use / name).</summary>
    None = 0,

    /// <summary>定长字节串。 / Fixed byte string.</summary>
    String = 1,

    /// <summary>数值（含宽度/符号/字节序/日期）。 / Numeric (width / signedness / endianness / date).</summary>
    Number = 2,

    /// <summary>正则。 / Regular expression.</summary>
    Regex = 3,

    /// <summary>有界范围搜索串。 / Bounded-range byte-string search.</summary>
    Search = 4,

    /// <summary>长度前缀串。 / Length-prefixed string.</summary>
    PString = 5,

    /// <summary>UTF-16LE 定长串。 / UTF-16LE fixed string.</summary>
    String16Le = 6,

    /// <summary>UTF-16BE 定长串。 / UTF-16BE fixed string.</summary>
    String16Be = 7,

    /// <summary>GUID（{...} 文本形态）。 / GUID textual form.</summary>
    Guid = 8,
}

/// <summary>
/// <para>数值类型的宽度、符号、字节序与日期编码，对应上游 file 的 in_type。</para>
/// Numeric width / signedness / endianness / date encoding, mirroring upstream file's in_type.
/// </summary>
public enum MagicNumberType : byte
{
    /// <summary>1 字节有符号。 / Signed byte.</summary>
    Byte = 0,

    /// <summary>1 字节无符号。 / Unsigned byte.</summary>
    UByte = 1,

    /// <summary>2 字节本机序有符号。 / Native-order signed short.</summary>
    Short = 2,

    /// <summary>2 字节本机序无符号。 / Native-order unsigned short.</summary>
    UShort = 3,

    /// <summary>4 字节本机序有符号。 / Native-order signed long.</summary>
    Long = 4,

    /// <summary>4 字节本机序无符号。 / Native-order unsigned long.</summary>
    ULong = 5,

    /// <summary>2 字节小端有符号。 / Little-endian signed short.</summary>
    Leshort = 6,

    /// <summary>2 字节小端无符号。 / Little-endian unsigned short.</summary>
    Uleshort = 7,

    /// <summary>2 字节大端有符号。 / Big-endian signed short.</summary>
    Beshort = 8,

    /// <summary>2 字节大端无符号。 / Big-endian unsigned short.</summary>
    Ubeshort = 9,

    /// <summary>4 字节小端有符号。 / Little-endian signed long.</summary>
    Lelong = 10,

    /// <summary>4 字节小端无符号。 / Little-endian unsigned long.</summary>
    Ulelong = 11,

    /// <summary>4 字节大端有符号。 / Big-endian signed long.</summary>
    Belong = 12,

    /// <summary>4 字节大端无符号。 / Big-endian unsigned long.</summary>
    Ubelong = 13,

    /// <summary>8 字节小端有符号。 / Little-endian signed quad.</summary>
    Lequad = 14,

    /// <summary>8 字节小端无符号。 / Little-endian unsigned quad.</summary>
    Ulequad = 15,

    /// <summary>8 字节大端有符号。 / Big-endian signed quad.</summary>
    Bequad = 16,

    /// <summary>8 字节大端无符号。 / Big-endian unsigned quad.</summary>
    Ubequad = 17,

    /// <summary>8 字节本机序有符号。 / Native-order signed quad.</summary>
    Quad = 18,

    /// <summary>8 字节本机序无符号。 / Native-order unsigned quad.</summary>
    Uquad = 19,

    /// <summary>4 字节大端日期。 / Big-endian date.</summary>
    Bedate = 20,

    /// <summary>4 字节小端日期。 / Little-endian date.</summary>
    Ledate = 21,

    /// <summary>4 字节本机序日期。 / Native-order date.</summary>
    Date = 22,
}

/// <summary>
/// <para>规则标志位（对齐上游 FILE_* 语义子集）。</para>
/// Rule flag bits (subset of upstream FILE_* semantics).
/// </summary>
[System.Flags]
public enum MagicFlags : uint
{
    /// <summary>无。 / None.</summary>
    None = 0,

    /// <summary>间接偏移（(x.type)）。 / Indirect offset ((x.type)).</summary>
    Indirect = 1 << 0,

    /// <summary>默认分支（default）。 / Default branch.</summary>
    Default = 1 << 1,

    /// <summary>偏移相对于父匹配结束位置（&amp;）。 / Offset relative to end of parent match (&amp;).</summary>
    RelativeToEnd = 1 << 2,

    /// <summary>偏移相对于文件尾。 / Offset measured from end of file.</summary>
    FromEnd = 1 << 3,

    /// <summary>结果取反（value 前置 !）。 / Negate the value test.</summary>
    Negate = 1 << 4,

    /// <summary>无空格拼接（\b 前缀）。 / No space before this fragment.</summary>
    NoSpace = 1 << 5,

    /// <summary>偏移值本身为相对间接基准的偏移。 / Offset is added to the indirect base.</summary>
    IndirectOffset = 1 << 6,

    /// <summary>命中后停止描述拼接（skip）。 / Consume without emitting (skip).</summary>
    Consume = 1 << 7,

    /// <summary>强制文本（regex/文本类型标记）。 / Force text.</summary>
    Text = 1 << 8,

    /// <summary>强制二进制（!：encoding=binary）。 / Force binary encoding.</summary>
    Binary = 1 << 9,
}

/// <summary>
/// <para>字符串匹配修饰（/c /W /w /f /T）。</para>
/// String match modifiers (/c /W /w /f /T).
/// </summary>
[System.Flags]
public enum MagicStringFlags : uint
{
    /// <summary>无。 / None.</summary>
    None = 0,

    /// <summary>忽略大小写。 / Case-insensitive.</summary>
    IgnoreCase = 1 << 0,

    /// <summary>空白折叠（/W）。 / Compact whitespace.</summary>
    CompactWhitespace = 1 << 1,

    /// <summary>可选空白（/w）。 / Optional whitespace.</summary>
    OptionalWhitespace = 1 << 2,

    /// <summary>整词匹配（/f）。 / Full word boundary.</summary>
    FullWord = 1 << 3,

    /// <summary>去除尾部空白（/T）。 / Trim trailing whitespace.</summary>
    Trim = 1 << 4,
}

/// <summary>
/// <para>正则修饰（/i /s /m /x）。</para>
/// Regex modifiers (/i /s /m /x).
/// </summary>
[System.Flags]
public enum MagicRegexFlags : uint
{
    /// <summary>无。 / None.</summary>
    None = 0,

    /// <summary>忽略大小写。 / Case-insensitive.</summary>
    IgnoreCase = 1 << 0,

    /// <summary>单行（. 匹配换行）。 / Dot matches newline.</summary>
    SingleLine = 1 << 1,

    /// <summary>多行（^ $ 匹配行边界）。 / Multiline anchors.</summary>
    Multiline = 1 << 2,
}

/// <summary>
/// <para>掩码运算（类型 token 上的 &amp; | ^ + - * / %）。</para>
/// Mask operator on a type token (&amp; | ^ + - * / %).
/// </summary>
public enum MagicMaskOp : byte
{
    /// <summary>无。 / None.</summary>
    None = 0,
    /// <summary>按位与。 / Bitwise and.</summary>
    And = 1,
    /// <summary>按位或。 / Bitwise or.</summary>
    Or = 2,
    /// <summary>按位异或。 / Bitwise xor.</summary>
    Xor = 3,
    /// <summary>加。 / Add.</summary>
    Add = 4,
    /// <summary>减。 / Subtract.</summary>
    Subtract = 5,
    /// <summary>乘。 / Multiply.</summary>
    Multiply = 6,
    /// <summary>除。 / Divide.</summary>
    Divide = 7,
    /// <summary>取模。 / Modulo.</summary>
    Modulo = 8,
}

/// <summary>
/// <para>数值比较关系。</para>
/// Numeric / value comparison relation.
/// </summary>
public enum MagicRelation : byte
{
    /// <summary>等于。 / Equal.</summary>
    Equal = 0,
    /// <summary>不等于。 / Not equal.</summary>
    NotEqual = 1,
    /// <summary>小于。 / Less than.</summary>
    LessThan = 2,
    /// <summary>大于。 / Greater than.</summary>
    GreaterThan = 3,
    /// <summary>按位与命中（&amp;）。 / Bit-and match.</summary>
    BitAnd = 4,
    /// <summary>按位异或（^）。 / Bit-xor match.</summary>
    BitXor = 5,
    /// <summary>任意（x）。 / Any value.</summary>
    Any = 6,
}
