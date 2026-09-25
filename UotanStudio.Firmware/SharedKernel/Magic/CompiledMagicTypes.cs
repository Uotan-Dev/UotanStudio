namespace UotanStudio.Firmware.SharedKernel.Magic;

/// <summary>
/// <para>编译型 magic 库的规则负载类别。</para>
/// Payload class of a compiled magic rule.
/// </summary>
public enum MagicKind : byte
{
    /// <summary>
    /// Rule payload class carrying no value, used by default, use and name-only rules.
    /// <para>不携带数值的规则负载类别，用于 default、use 与仅名称规则。</para>
    /// </summary>
    None = 0,

    /// <summary>
    /// Fixed-length byte string compared byte by byte against the rule pattern.
    /// <para>定长字节串，与规则模式逐字节比对。</para>
    /// </summary>
    String = 1,

    /// <summary>
    /// Numeric value whose width, signedness and byte order come from <see cref="MagicNumberType"/>.
    /// <para>数值负载，其宽度、符号与字节序由 <see cref="MagicNumberType"/> 决定。</para>
    /// </summary>
    Number = 2,

    /// <summary>
    /// Regular expression matched against the window from the rule offset to its end.
    /// <para>正则表达式，从规则偏移起匹配到窗口末尾。</para>
    /// </summary>
    Regex = 3,

    /// <summary>
    /// Byte-string search bounded by the rule range, defaulting to 100 bytes; a 0xFF pattern byte matches any byte.
    /// <para>受规则范围限制的字节串搜索，范围缺省为 100 字节；模式字节 0xFF 匹配任意字节。</para>
    /// </summary>
    Search = 4,

    /// <summary>
    /// Length-prefixed string, where the prefix is read as 1, 2 or 4 bytes and any other size falls back to 1.
    /// <para>带长度前缀的字符串，前缀按 1、2 或 4 字节读取，其它宽度一律退回 1 字节。</para>
    /// </summary>
    PString = 5,

    /// <summary>
    /// Fixed-length UTF-16 little-endian string.
    /// <para>定长 UTF-16LE 字符串。</para>
    /// </summary>
    String16Le = 6,

    /// <summary>
    /// Fixed-length UTF-16 big-endian string.
    /// <para>定长 UTF-16BE 字符串。</para>
    /// </summary>
    String16Be = 7,

    /// <summary>
    /// GUID rule: the source value is textual, while the compiled pattern holds 16 raw bytes and 0xFF wildcards a byte.
    /// <para>GUID 规则：源值以文本书写，编译后的模式保存 16 个原始字节，其中 0xFF 表示通配。</para>
    /// </summary>
    Guid = 8,
}

/// <summary>
/// <para>数值类型的宽度、符号、字节序与日期编码，对应上游 file 的 in_type。</para>
/// Numeric width / signedness / endianness / date encoding, mirroring upstream file's in_type.
/// </summary>
public enum MagicNumberType : byte
{
    /// <summary>Signed 1-byte value. <para>1 字节有符号整数。</para></summary>
    Byte = 0,

    /// <summary>Unsigned 1-byte value. <para>1 字节无符号整数。</para></summary>
    UByte = 1,

    /// <summary>
    /// 2-byte signed short written as host order in the rule source; the matcher decodes it little-endian.
    /// <para>规则源写作本机序的 2 字节有符号短整数；匹配时按小端解码。</para>
    /// </summary>
    Short = 2,

    /// <summary>
    /// 2-byte unsigned short written as host order in the rule source; the matcher decodes it little-endian.
    /// <para>规则源写作本机序的 2 字节无符号短整数；匹配时按小端解码。</para>
    /// </summary>
    UShort = 3,

    /// <summary>
    /// 4-byte signed integer written as host order in the rule source; the matcher decodes it little-endian.
    /// <para>规则源写作本机序的 4 字节有符号整数；匹配时按小端解码。</para>
    /// </summary>
    Long = 4,

    /// <summary>
    /// 4-byte unsigned integer written as host order in the rule source; the matcher decodes it little-endian.
    /// <para>规则源写作本机序的 4 字节无符号整数；匹配时按小端解码。</para>
    /// </summary>
    ULong = 5,

    /// <summary>Signed 2-byte short in little-endian order. <para>小端字节序的 2 字节有符号短整数。</para></summary>
    Leshort = 6,

    /// <summary>Unsigned 2-byte short in little-endian order. <para>小端字节序的 2 字节无符号短整数。</para></summary>
    Uleshort = 7,

    /// <summary>Signed 2-byte short in big-endian order. <para>大端字节序的 2 字节有符号短整数。</para></summary>
    Beshort = 8,

    /// <summary>Unsigned 2-byte short in big-endian order. <para>大端字节序的 2 字节无符号短整数。</para></summary>
    Ubeshort = 9,

    /// <summary>Signed 4-byte integer in little-endian order. <para>小端字节序的 4 字节有符号整数。</para></summary>
    Lelong = 10,

    /// <summary>Unsigned 4-byte integer in little-endian order. <para>小端字节序的 4 字节无符号整数。</para></summary>
    Ulelong = 11,

    /// <summary>Signed 4-byte integer in big-endian order. <para>大端字节序的 4 字节有符号整数。</para></summary>
    Belong = 12,

    /// <summary>Unsigned 4-byte integer in big-endian order. <para>大端字节序的 4 字节无符号整数。</para></summary>
    Ubelong = 13,

    /// <summary>Signed 8-byte integer in little-endian order. <para>小端字节序的 8 字节有符号整数。</para></summary>
    Lequad = 14,

    /// <summary>Unsigned 8-byte integer in little-endian order. <para>小端字节序的 8 字节无符号整数。</para></summary>
    Ulequad = 15,

    /// <summary>Signed 8-byte integer in big-endian order. <para>大端字节序的 8 字节有符号整数。</para></summary>
    Bequad = 16,

    /// <summary>Unsigned 8-byte integer in big-endian order. <para>大端字节序的 8 字节无符号整数。</para></summary>
    Ubequad = 17,

    /// <summary>
    /// 8-byte signed integer written as host order in the rule source; the matcher decodes it little-endian.
    /// <para>规则源写作本机序的 8 字节有符号整数；匹配时按小端解码。</para>
    /// </summary>
    Quad = 18,

    /// <summary>
    /// 8-byte unsigned integer written as host order in the rule source; the matcher decodes it little-endian.
    /// <para>规则源写作本机序的 8 字节无符号整数；匹配时按小端解码。</para>
    /// </summary>
    Uquad = 19,

    /// <summary>
    /// Signed 4-byte big-endian value rendered as a date.
    /// <para>大端字节序的 4 字节有符号值，按日期渲染。</para>
    /// </summary>
    Bedate = 20,

    /// <summary>
    /// Signed 4-byte little-endian value rendered as a date.
    /// <para>小端字节序的 4 字节有符号值，按日期渲染。</para>
    /// </summary>
    Ledate = 21,

    /// <summary>
    /// Signed 4-byte date written as host order in the rule source; the matcher decodes it little-endian.
    /// <para>规则源写作本机序的 4 字节日期值；匹配时按小端解码。</para>
    /// </summary>
    Date = 22,
}

/// <summary>
/// <para>规则标志位（对齐上游 FILE_* 语义子集）。</para>
/// Rule flag bits (subset of upstream FILE_* semantics).
/// </summary>
[System.Flags]
public enum MagicFlags : uint
{
    /// <summary>No rule flag is set. <para>未设置任何规则标志位。</para></summary>
    None = 0,

    /// <summary>
    /// The offset field names a stored number which, after its optional mask step, becomes the real match offset.
    /// <para>偏移字段指向一个已存储的数值，该数值在完成可选掩码运算后作为真正的匹配偏移。</para>
    /// </summary>
    Indirect = 1 << 0,

    /// <summary>
    /// Fallback branch of a level: used only after the concrete rules at the same offset fail, never at the top level.
    /// <para>某层级的兜底分支：仅在同一偏移的具体规则全部失败后才参与求值，顶层不使用。</para>
    /// </summary>
    Default = 1 << 1,

    /// <summary>
    /// Nested rules count their offset from the end of the parent match (&amp;) instead of from its start.
    /// <para>嵌套规则的偏移从父匹配的结束位置（&amp;）起算，而非起始位置。</para>
    /// </summary>
    RelativeToEnd = 1 << 2,

    /// <summary>Offset is added to the file length, measuring from end of file. <para>偏移与文件长度相加，即自文件末尾起算。</para></summary>
    FromEnd = 1 << 3,

    /// <summary>A successful value test is treated as a non-match. <para>值测试命中时按未命中处理。</para></summary>
    Negate = 1 << 4,

    /// <summary>No space is joined before this description fragment. <para>该描述片段前不拼接空格。</para></summary>
    NoSpace = 1 << 5,

    /// <summary>
    /// Flag bit defined for parity with the upstream dialect; neither the rule generator nor the matcher tests it.
    /// <para>为与上游方言对齐而定义的标志位；规则生成与匹配流程均不读取。</para>
    /// </summary>
    IndirectOffset = 1 << 6,

    /// <summary>Match is consumed without emitting description text. <para>命中后仅消耗，不输出描述文本。</para></summary>
    Consume = 1 << 7,

    /// <summary>
    /// Flag bit recording an upstream force-text request; the compiled matcher does not act on it.
    /// <para>记录上游强制文本意图的标志位；编译后的匹配器不据此改变行为。</para>
    /// </summary>
    Text = 1 << 8,

    /// <summary>
    /// Flag bit recording an upstream !:encoding=binary request; the compiled matcher does not act on it.
    /// <para>记录上游 !:encoding=binary 请求的标志位；编译后的匹配器不据此改变行为。</para>
    /// </summary>
    Binary = 1 << 9,
}

/// <summary>
/// <para>字符串匹配修饰（/c /W /w /f /T）。</para>
/// String match modifiers (/c /W /w /f /T).
/// </summary>
[System.Flags]
public enum MagicStringFlags : uint
{
    /// <summary>No string modifier is set. <para>未设置任何字符串修饰位。</para></summary>
    None = 0,

    /// <summary>Matching is case-insensitive within the ASCII A-Z range. <para>在 ASCII A-Z 范围内按大小写不敏感匹配。</para></summary>
    IgnoreCase = 1 << 0,

    /// <summary>
    /// A whitespace byte in the pattern requires and consumes a whitespace run in the data (/W).
    /// <para>模式中的空白字节要求并消耗数据中的一段连续空白（/W）。</para>
    /// </summary>
    CompactWhitespace = 1 << 1,

    /// <summary>
    /// A whitespace byte in the pattern matches zero or more whitespace bytes in the data (/w).
    /// <para>模式中的空白字节匹配数据里的零个或多个空白（/w）。</para>
    /// </summary>
    OptionalWhitespace = 1 << 2,

    /// <summary>
    /// The byte following the match must be whitespace or the end of the window (/f).
    /// <para>匹配之后的下一个字节必须是空白，或已到窗口末尾（/f）。</para>
    /// </summary>
    FullWord = 1 << 3,

    /// <summary>Trailing whitespace is dropped from the decoded value (/T). <para>解码结果尾部空白会被去掉（/T）。</para></summary>
    Trim = 1 << 4,
}

/// <summary>
/// <para>正则修饰（/i /s /m /x）。</para>
/// Regex modifiers (/i /s /m /x).
/// </summary>
[System.Flags]
public enum MagicRegexFlags : uint
{
    /// <summary>No regex modifier is set. <para>未设置任何正则修饰位。</para></summary>
    None = 0,

    /// <summary>
    /// Adds <c>RegexOptions.IgnoreCase</c> to the culture-invariant options the pattern is compiled with.
    /// <para>在模式编译所用的 CultureInvariant 选项之外追加 <c>RegexOptions.IgnoreCase</c>。</para>
    /// </summary>
    IgnoreCase = 1 << 0,

    /// <summary>
    /// Adds <c>RegexOptions.Singleline</c>, so the dot also matches line breaks.
    /// <para>追加 <c>RegexOptions.Singleline</c>，使圆点也能匹配换行符。</para>
    /// </summary>
    SingleLine = 1 << 1,

    /// <summary>
    /// Adds <c>RegexOptions.Multiline</c>, so the anchors match line boundaries instead of the whole window.
    /// <para>追加 <c>RegexOptions.Multiline</c>，使定位符匹配行边界而非整个窗口。</para>
    /// </summary>
    Multiline = 1 << 2,
}

/// <summary>
/// <para>掩码运算（类型 token 上的 &amp; | ^ + - * / %）。</para>
/// Mask operator on a type token (&amp; | ^ + - * / %).
/// </summary>
public enum MagicMaskOp : byte
{
    /// <summary>Value passes the mask step untouched. <para>数值原样通过掩码步骤。</para></summary>
    None = 0,
    /// <summary>Bitwise and of value and mask. <para>数值与掩码按位与。</para></summary>
    And = 1,
    /// <summary>Bitwise or of value and mask. <para>数值与掩码按位或。</para></summary>
    Or = 2,
    /// <summary>Bitwise xor of value and mask. <para>数值与掩码按位异或。</para></summary>
    Xor = 3,
    /// <summary>Mask added to the value. <para>数值加上掩码。</para></summary>
    Add = 4,
    /// <summary>Mask subtracted from the value. <para>数值减去掩码。</para></summary>
    Subtract = 5,
    /// <summary>Value multiplied by the mask. <para>数值乘以掩码。</para></summary>
    Multiply = 6,
    /// <summary>
    /// Value divided by the mask; a zero mask leaves the value untouched instead of failing.
    /// <para>数值除以掩码；掩码为 0 时保持数值不变而不报错。</para>
    /// </summary>
    Divide = 7,
    /// <summary>
    /// Remainder of the value divided by the mask; a zero mask leaves the value untouched instead of failing.
    /// <para>数值对掩码取模；掩码为 0 时保持数值不变而不报错。</para>
    /// </summary>
    Modulo = 8,
}

/// <summary>
/// <para>数值比较关系。</para>
/// Numeric / value comparison relation.
/// </summary>
public enum MagicRelation : byte
{
    /// <summary>
    /// Value equals the expected value, comparing only the bits kept by the type width.
    /// <para>数值与期望值相等；比较范围仅限类型宽度保留的位。</para>
    /// </summary>
    Equal = 0,
    /// <summary>
    /// Value differs from the expected value; fixed byte-string rules never match under this relation.
    /// <para>数值与期望值不相等；定长字节串规则在该关系下永不命中。</para>
    /// </summary>
    NotEqual = 1,
    /// <summary>
    /// Value is below the expected value: unsigned types compare as unsigned, signed types after sign extension.
    /// <para>数值小于期望值：无符号类型按无符号比较，有符号类型先符号扩展再比较。</para>
    /// </summary>
    LessThan = 2,
    /// <summary>
    /// Value is above the expected value: unsigned types compare as unsigned, signed types after sign extension.
    /// <para>数值大于期望值：无符号类型按无符号比较，有符号类型先符号扩展再比较。</para>
    /// </summary>
    GreaterThan = 3,
    /// <summary>
    /// Every bit set in the expected value is also set in the value, that is (value &amp; expected) == expected.
    /// <para>期望值中置位的每一位在数值里也必须置位，即 (value &amp; expected) == expected。</para>
    /// </summary>
    BitAnd = 4,
    /// <summary>
    /// The xor of value and expected equals the expected value, that is (value ^ expected) == expected.
    /// <para>数值与期望值异或的结果等于期望值，即 (value ^ expected) == expected。</para>
    /// </summary>
    BitXor = 5,
    /// <summary>
    /// Presence test: matches whenever the payload can be read; text payloads run to the next NUL or line break.
    /// <para>存在性测试：只要能读出负载即命中；文本负载一直取到下一个 NUL 或换行。</para>
    /// </summary>
    Any = 6,
}