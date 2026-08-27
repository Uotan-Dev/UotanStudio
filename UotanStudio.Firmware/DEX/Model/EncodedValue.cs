namespace UotanStudio.Firmware.Dex.Model;

/// <summary>
/// <para>encoded_value 的值类型（VALUE_*，按 Google dex-format 定义）。</para>
/// encoded_value value types (VALUE_*, per Google dex-format).
/// </summary>
public enum EncodedValueType : byte
{
    /// <summary>单字节有符号整数（arg=0）。<para>One-byte signed integer (arg=0).</para></summary>
    Byte = 0x00,
    /// <summary>有符号短整数（arg=0..1，零扩展后符号扩展）。<para>Signed short (arg=0..1, zero-extended then sign-extended).</para></summary>
    Short = 0x02,
    /// <summary>无符号字符（arg=0..1）。<para>Unsigned char (arg=0..1).</para></summary>
    Char = 0x03,
    /// <summary>有符号整数（arg=0..3）。<para>Signed int (arg=0..3).</para></summary>
    Int = 0x04,
    /// <summary>有符号长整数（arg=0..7）。<para>Signed long (arg=0..7).</para></summary>
    Long = 0x06,
    /// <summary>IEEE754 单精度浮点（arg=0..3，右零扩展）。<para>IEEE754 single (arg=0..3, right zero-extended).</para></summary>
    Float = 0x10,
    /// <summary>IEEE754 双精度浮点（arg=0..7，右零扩展）。<para>IEEE754 double (arg=0..7, right zero-extended).</para></summary>
    Double = 0x11,
    /// <summary>方法原型索引（proto_ids）。<para>Method prototype index (proto_ids).</para></summary>
    MethodType = 0x15,
    /// <summary>方法句柄索引（method_handles）。<para>Method handle index (method_handles).</para></summary>
    MethodHandle = 0x16,
    /// <summary>字符串索引（string_ids）。<para>String index (string_ids).</para></summary>
    String = 0x17,
    /// <summary>类型索引（type_ids）。<para>Type index (type_ids).</para></summary>
    Type = 0x18,
    /// <summary>字段索引（field_ids）。<para>Field index (field_ids).</para></summary>
    Field = 0x19,
    /// <summary>方法索引（method_ids）。<para>Method index (method_ids).</para></summary>
    Method = 0x1a,
    /// <summary>枚举常量字段索引（field_ids）。<para>Enum constant field index (field_ids).</para></summary>
    Enum = 0x1b,
    /// <summary>值数组（encoded_array）。<para>Value array (encoded_array).</para></summary>
    Array = 0x1c,
    /// <summary>注解（encoded_annotation）。<para>Annotation (encoded_annotation).</para></summary>
    Annotation = 0x1d,
    /// <summary>空值（无数据）。<para>Null (no data).</para></summary>
    Null = 0x1e,
    /// <summary>布尔（arg=0..1）。<para>Boolean (arg=0..1).</para></summary>
    Boolean = 0x1f
}

/// <summary>
/// <para>编码值（encoded_value）：一个值类型 + 承载的值。</para>
/// Encoded value (encoded_value): a value type plus the carried value.
/// </summary>
/// <remarks>
/// <para>各类型承载的值：</para>
/// <list type="bullet">
/// <item><description>Byte/Short/Char/Int/Long → 对应 CLR 有符号类型（char 为 char）。</description></item>
/// <item><description>Float/Double → float/double。</description></item>
/// <item><description>MethodType/MethodHandle/String/Type/Field/Method/Enum → <c>int</c> 索引。</description></item>
/// <item><description>Array → <c>EncodedValue[]</c>。</description></item>
/// <item><description>Annotation → <see cref="AnnotationItem"/>。</description></item>
/// <item><description>Null → null。</description></item>
/// <item><description>Boolean → bool。</description></item>
/// </list>
/// <para>Carried values per type: Byte/Short/Char/Int/Long → the corresponding CLR signed type
/// (char as char); Float/Double → float/double; MethodType/MethodHandle/String/Type/Field/Method/Enum →
/// an <c>int</c> index; Array → <c>EncodedValue[]</c>; Annotation → <see cref="AnnotationItem"/>;
/// Null → null; Boolean → bool.</para>
/// </remarks>
public sealed class EncodedValue
{
    /// <summary>值类型。<para>Value type.</para></summary>
    public EncodedValueType Type { get; init; }

    /// <summary>承载的值（见类型说明）。<para>The carried value (see type notes).</para></summary>
    public object? Value { get; init; }

    /// <summary>构造编码值。<para>Creates an encoded value.</para></summary>
    public EncodedValue(EncodedValueType type, object? value)
    {
        Type = type;
        Value = value;
    }

    /// <summary>构造空值（VALUE_NULL）。<para>Creates a null value (VALUE_NULL).</para></summary>
    public static EncodedValue Null() => new(EncodedValueType.Null, null);

    /// <summary>构造布尔值（VALUE_BOOLEAN）。<para>Creates a boolean value (VALUE_BOOLEAN).</para></summary>
    public static EncodedValue Boolean(bool value) => new(EncodedValueType.Boolean, value);

    /// <summary>构造整数值（VALUE_INT）。<para>Creates an int value (VALUE_INT).</para></summary>
    public static EncodedValue Int(int value) => new(EncodedValueType.Int, value);

    /// <summary>构造长整数值（VALUE_LONG）。<para>Creates a long value (VALUE_LONG).</para></summary>
    public static EncodedValue Long(long value) => new(EncodedValueType.Long, value);

    /// <summary>构造短整数值（VALUE_SHORT）。<para>Creates a short value (VALUE_SHORT).</para></summary>
    public static EncodedValue Short(short value) => new(EncodedValueType.Short, value);

    /// <summary>构造字节值（VALUE_BYTE）。<para>Creates a byte value (VALUE_BYTE).</para></summary>
    public static EncodedValue Byte(byte value) => new(EncodedValueType.Byte, value);

    /// <summary>构造字符值（VALUE_CHAR）。<para>Creates a char value (VALUE_CHAR).</para></summary>
    public static EncodedValue Char(char value) => new(EncodedValueType.Char, value);

    /// <summary>构造浮点值（VALUE_FLOAT）。<para>Creates a float value (VALUE_FLOAT).</para></summary>
    public static EncodedValue Float(float value) => new(EncodedValueType.Float, value);

    /// <summary>构造双精度值（VALUE_DOUBLE）。<para>Creates a double value (VALUE_DOUBLE).</para></summary>
    public static EncodedValue Double(double value) => new(EncodedValueType.Double, value);

    /// <summary>构造字符串索引值（VALUE_STRING）。<para>Creates a string-index value (VALUE_STRING).</para></summary>
    public static EncodedValue String(int index) => new(EncodedValueType.String, index);

    /// <summary>构造类型索引值（VALUE_TYPE）。<para>Creates a type-index value (VALUE_TYPE).</para></summary>
    public static EncodedValue TypeRef(int index) => new(EncodedValueType.Type, index);

    /// <summary>构造字段索引值（VALUE_FIELD）。<para>Creates a field-index value (VALUE_FIELD).</para></summary>
    public static EncodedValue Field(int index) => new(EncodedValueType.Field, index);

    /// <summary>构造方法索引值（VALUE_METHOD）。<para>Creates a method-index value (VALUE_METHOD).</para></summary>
    public static EncodedValue Method(int index) => new(EncodedValueType.Method, index);

    /// <summary>构造枚举索引值（VALUE_ENUM，值为 field_ids 索引）。<para>Creates an enum-index value (VALUE_ENUM; a field_ids index).</para></summary>
    public static EncodedValue Enum(int index) => new(EncodedValueType.Enum, index);

    /// <summary>构造值数组（VALUE_ARRAY）。<para>Creates a value array (VALUE_ARRAY).</para></summary>
    public static EncodedValue Array(params EncodedValue[] items) => new(EncodedValueType.Array, items);

    /// <summary>构造注解值（VALUE_ANNOTATION）。<para>Creates an annotation value (VALUE_ANNOTATION).</para></summary>
    public static EncodedValue Annotation(AnnotationItem item) => new(EncodedValueType.Annotation, item);

    /// <summary>
    /// <para>便捷取值：布尔值。</para>
    /// Convenience accessor: boolean value.
    /// </summary>
    public bool AsBoolean() => Value is bool b && b;

    /// <summary>
    /// <para>便捷取值：整数（Int/Short/Byte/Char 统一转 int）。</para>
    /// Convenience accessor: integral value (Int/Short/Byte/Char unified to int).
    /// </summary>
    public int AsInt() => Value switch
    {
        int i => i,
        short s => s,
        byte b => b,
        char c => c,
        _ => 0
    };

    /// <summary>
    /// <para>便捷取值：长整数。</para>
    /// Convenience accessor: long value.
    /// </summary>
    public long AsLong() => Value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        _ => 0
    };

    /// <summary>
    /// <para>便捷取值：索引值（String/Type/Field/Method/Enum 等）。</para>
    /// Convenience accessor: index value (String/Type/Field/Method/Enum etc.).
    /// </summary>
    public int AsIndex() => Value is int i ? i : -1;

    /// <summary>
    /// <para>便捷取值：值数组。</para>
    /// Convenience accessor: value array.
    /// </summary>
    public EncodedValue[]? AsArray() => Value as EncodedValue[];
}