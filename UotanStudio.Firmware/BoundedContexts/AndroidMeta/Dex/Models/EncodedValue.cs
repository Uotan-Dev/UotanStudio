namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

/// <summary>
/// DEX <c>encoded_value_type</c> tag identifying how an annotation value is encoded.
/// <para>DEX 的 <c>encoded_value_type</c> 标签，标识注解值的编码方式。</para>
/// </summary>
public enum EncodedValueType : byte
{
    /// <summary>VALUE_BYTE (0x00); payload is a <c>byte</c>. <para>VALUE_BYTE（0x00）；负载为 <c>byte</c>。</para></summary>
    Byte = 0x00,

    /// <summary>VALUE_SHORT (0x02); payload is a <c>short</c>. <para>VALUE_SHORT（0x02）；负载为 <c>short</c>。</para></summary>
    Short = 0x02,

    /// <summary>VALUE_CHAR (0x03); payload is a <c>char</c>. <para>VALUE_CHAR（0x03）；负载为 <c>char</c>。</para></summary>
    Char = 0x03,

    /// <summary>VALUE_INT (0x04); payload is an <c>int</c>. <para>VALUE_INT（0x04）；负载为 <c>int</c>。</para></summary>
    Int = 0x04,

    /// <summary>VALUE_LONG (0x06); payload is a <c>long</c>. <para>VALUE_LONG（0x06）；负载为 <c>long</c>。</para></summary>
    Long = 0x06,

    /// <summary>VALUE_FLOAT (0x10); payload is a <c>float</c>. <para>VALUE_FLOAT（0x10）；负载为 <c>float</c>。</para></summary>
    Float = 0x10,

    /// <summary>VALUE_DOUBLE (0x11); payload is a <c>double</c>. <para>VALUE_DOUBLE（0x11）；负载为 <c>double</c>。</para></summary>
    Double = 0x11,

    /// <summary>VALUE_METHOD_TYPE (0x15); payload is a <c>proto_ids</c> index held as an <c>int</c>. <para>VALUE_METHOD_TYPE（0x15）；负载为以 <c>int</c> 保存的 <c>proto_ids</c> 索引。</para></summary>
    MethodType = 0x15,

    /// <summary>VALUE_METHOD_HANDLE (0x16); payload is a <c>method_handles</c> index held as an <c>int</c>. <para>VALUE_METHOD_HANDLE（0x16）；负载为以 <c>int</c> 保存的 <c>method_handles</c> 索引。</para></summary>
    MethodHandle = 0x16,

    /// <summary>VALUE_STRING (0x17); payload is a <c>string_ids</c> index held as an <c>int</c>. <para>VALUE_STRING（0x17）；负载为以 <c>int</c> 保存的 <c>string_ids</c> 索引。</para></summary>
    String = 0x17,

    /// <summary>VALUE_TYPE (0x18); payload is a <c>type_ids</c> index held as an <c>int</c>. <para>VALUE_TYPE（0x18）；负载为以 <c>int</c> 保存的 <c>type_ids</c> 索引。</para></summary>
    Type = 0x18,

    /// <summary>VALUE_FIELD (0x19); payload is a <c>field_ids</c> index held as an <c>int</c>. <para>VALUE_FIELD（0x19）；负载为以 <c>int</c> 保存的 <c>field_ids</c> 索引。</para></summary>
    Field = 0x19,

    /// <summary>VALUE_METHOD (0x1a); payload is a <c>method_ids</c> index held as an <c>int</c>. <para>VALUE_METHOD（0x1a）；负载为以 <c>int</c> 保存的 <c>method_ids</c> 索引。</para></summary>
    Method = 0x1a,

    /// <summary>VALUE_ENUM (0x1b); payload is the <c>field_ids</c> index of the enum constant, held as an <c>int</c>. <para>VALUE_ENUM（0x1b）；负载为枚举常量的 <c>field_ids</c> 索引，以 <c>int</c> 保存。</para></summary>
    Enum = 0x1b,

    /// <summary>VALUE_ARRAY (0x1c); payload is an <c>EncodedValue[]</c> of element values. <para>VALUE_ARRAY（0x1c）；负载为包含元素值的 <c>EncodedValue[]</c>。</para></summary>
    Array = 0x1c,

    /// <summary>VALUE_ANNOTATION (0x1d); payload is an <c>AnnotationItem</c>. <para>VALUE_ANNOTATION（0x1d）；负载为 <c>AnnotationItem</c>。</para></summary>
    Annotation = 0x1d,

    /// <summary>VALUE_NULL (0x1e); the payload is <see langword="null"/>. <para>VALUE_NULL（0x1e）；负载为 <see langword="null"/>。</para></summary>
    Null = 0x1e,

    /// <summary>VALUE_BOOLEAN (0x1f); payload is a <c>bool</c>. <para>VALUE_BOOLEAN（0x1f）；负载为 <c>bool</c>。</para></summary>
    Boolean = 0x1f
}

/// <summary>
/// A decoded or to-be-encoded DEX <c>encoded_value</c>: a tag plus its payload, as used in annotations and static field initializers.
/// <para>已解码或待编码的 DEX <c>encoded_value</c>：标签加负载，用于注解和静态字段初始化值。</para>
/// </summary>
public sealed class EncodedValue
{
    /// <summary>The value's encoding tag.
    /// <para>值的编码标签。</para></summary>
    /// <value>One of the <see cref="EncodedValueType"/> tags; it decides how <see cref="Value"/> is interpreted by readers and writers.
    /// <para><see cref="EncodedValueType"/> 之一；它决定读写方如何解释 <see cref="Value"/>。</para></value>
    public EncodedValueType Type { get; init; }

    /// <summary>The decoded payload.
    /// <para>已解码的负载。</para></summary>
    /// <value>The CLR type follows <see cref="Type"/>: primitives for numeric/boolean tags, an <c>int</c> index for <c>String</c>/<c>Type</c>/<c>Field</c>/<c>Method</c>/<c>Enum</c>/<c>MethodType</c>/<c>MethodHandle</c>, an <see cref="EncodedValue"/> array for <c>Array</c>, an <c>AnnotationItem</c> for <c>Annotation</c>, and <see langword="null"/> for <c>Null</c>.
    /// <para>负载的 CLR 类型随 <see cref="Type"/> 而定：数值/布尔标签为对应基元类型，<c>String</c>/<c>Type</c>/<c>Field</c>/<c>Method</c>/<c>Enum</c>/<c>MethodType</c>/<c>MethodHandle</c> 为 <c>int</c> 索引，<c>Array</c> 为 <see cref="EncodedValue"/> 数组，<c>Annotation</c> 为 <c>AnnotationItem</c>，<c>Null</c> 为 <see langword="null"/>。</para></value>
    public object? Value { get; init; }

    /// <summary>
    /// Creates an encoded value.
    /// <para>构造编码值。</para>
    /// </summary>
    /// <param name="type">The encoding tag. <para>编码标签。</para></param>
    /// <param name="value">The payload; must match the CLR type implied by <paramref name="type"/>. <para>负载；必须与 <paramref name="type"/> 所隐含的 CLR 类型一致。</para></param>
    public EncodedValue(EncodedValueType type, object? value)
    {
        Type = type;
        Value = value;
    }

    /// <summary>
    /// Creates a null value (VALUE_NULL).
    /// <para>构造空值（VALUE_NULL）。</para>
    /// </summary>
    /// <returns>An encoded value whose payload is <see langword="null"/>. <para>负载为 <see langword="null"/> 的编码值。</para></returns>
    public static EncodedValue Null() => new(EncodedValueType.Null, null);

    /// <summary>
    /// Creates a boolean value (VALUE_BOOLEAN).
    /// <para>构造布尔值（VALUE_BOOLEAN）。</para>
    /// </summary>
    /// <param name="value">The boolean payload. <para>布尔负载。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Boolean"/>. <para>标签为 <see cref="EncodedValueType.Boolean"/> 的编码值。</para></returns>
    public static EncodedValue Boolean(bool value) => new(EncodedValueType.Boolean, value);

    /// <summary>
    /// Creates an int value (VALUE_INT).
    /// <para>构造整数值（VALUE_INT）。</para>
    /// </summary>
    /// <param name="value">The 32-bit payload. <para>32 位负载。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Int"/>. <para>标签为 <see cref="EncodedValueType.Int"/> 的编码值。</para></returns>
    public static EncodedValue Int(int value) => new(EncodedValueType.Int, value);

    /// <summary>
    /// Creates a long value (VALUE_LONG).
    /// <para>构造长整数值（VALUE_LONG）。</para>
    /// </summary>
    /// <param name="value">The 64-bit payload. <para>64 位负载。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Long"/>. <para>标签为 <see cref="EncodedValueType.Long"/> 的编码值。</para></returns>
    public static EncodedValue Long(long value) => new(EncodedValueType.Long, value);

    /// <summary>
    /// Creates a short value (VALUE_SHORT).
    /// <para>构造短整数值（VALUE_SHORT）。</para>
    /// </summary>
    /// <param name="value">The 16-bit payload. <para>16 位负载。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Short"/>. <para>标签为 <see cref="EncodedValueType.Short"/> 的编码值。</para></returns>
    public static EncodedValue Short(short value) => new(EncodedValueType.Short, value);

    /// <summary>
    /// Creates a byte value (VALUE_BYTE).
    /// <para>构造字节值（VALUE_BYTE）。</para>
    /// </summary>
    /// <param name="value">The 8-bit payload. <para>8 位负载。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Byte"/>. <para>标签为 <see cref="EncodedValueType.Byte"/> 的编码值。</para></returns>
    public static EncodedValue Byte(byte value) => new(EncodedValueType.Byte, value);

    /// <summary>
    /// Creates a char value (VALUE_CHAR).
    /// <para>构造字符值（VALUE_CHAR）。</para>
    /// </summary>
    /// <param name="value">The UTF-16 code unit payload. <para>UTF-16 码元负载。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Char"/>. <para>标签为 <see cref="EncodedValueType.Char"/> 的编码值。</para></returns>
    public static EncodedValue Char(char value) => new(EncodedValueType.Char, value);

    /// <summary>
    /// Creates a float value (VALUE_FLOAT).
    /// <para>构造浮点值（VALUE_FLOAT）。</para>
    /// </summary>
    /// <param name="value">The single-precision payload. <para>单精度负载。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Float"/>. <para>标签为 <see cref="EncodedValueType.Float"/> 的编码值。</para></returns>
    public static EncodedValue Float(float value) => new(EncodedValueType.Float, value);

    /// <summary>
    /// Creates a double value (VALUE_DOUBLE).
    /// <para>构造双精度值（VALUE_DOUBLE）。</para>
    /// </summary>
    /// <param name="value">The double-precision payload. <para>双精度负载。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Double"/>. <para>标签为 <see cref="EncodedValueType.Double"/> 的编码值。</para></returns>
    public static EncodedValue Double(double value) => new(EncodedValueType.Double, value);

    /// <summary>
    /// Creates a string-index value (VALUE_STRING).
    /// <para>构造字符串索引值（VALUE_STRING）。</para>
    /// </summary>
    /// <param name="index">Zero-based <c>string_ids</c> index; not validated against the table here. <para>从零开始的 <c>string_ids</c> 索引；此处不对照字符串表校验。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.String"/> with the index as payload. <para>标签为 <see cref="EncodedValueType.String"/>、负载为索引的编码值。</para></returns>
    public static EncodedValue String(int index) => new(EncodedValueType.String, index);

    /// <summary>
    /// Creates a type-index value (VALUE_TYPE).
    /// <para>构造类型索引值（VALUE_TYPE）。</para>
    /// </summary>
    /// <param name="index">Zero-based <c>type_ids</c> index; not validated against the table here. <para>从零开始的 <c>type_ids</c> 索引；此处不对照类型表校验。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Type"/> with the index as payload. <para>标签为 <see cref="EncodedValueType.Type"/>、负载为索引的编码值。</para></returns>
    public static EncodedValue TypeRef(int index) => new(EncodedValueType.Type, index);

    /// <summary>
    /// Creates a field-index value (VALUE_FIELD).
    /// <para>构造字段索引值（VALUE_FIELD）。</para>
    /// </summary>
    /// <param name="index">Zero-based <c>field_ids</c> index; not validated against the table here. <para>从零开始的 <c>field_ids</c> 索引；此处不对照字段表校验。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Field"/> with the index as payload. <para>标签为 <see cref="EncodedValueType.Field"/>、负载为索引的编码值。</para></returns>
    public static EncodedValue Field(int index) => new(EncodedValueType.Field, index);

    /// <summary>
    /// Creates a method-index value (VALUE_METHOD).
    /// <para>构造方法索引值（VALUE_METHOD）。</para>
    /// </summary>
    /// <param name="index">Zero-based <c>method_ids</c> index; not validated against the table here. <para>从零开始的 <c>method_ids</c> 索引；此处不对照方法表校验。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Method"/> with the index as payload. <para>标签为 <see cref="EncodedValueType.Method"/>、负载为索引的编码值。</para></returns>
    public static EncodedValue Method(int index) => new(EncodedValueType.Method, index);

    /// <summary>
    /// Creates an enum-index value (VALUE_ENUM; a field_ids index).
    /// <para>构造枚举索引值（VALUE_ENUM，值为 field_ids 索引）。</para>
    /// </summary>
    /// <param name="index">Zero-based <c>field_ids</c> index of the enum constant. <para>枚举常量的从零开始 <c>field_ids</c> 索引。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Enum"/> with the index as payload. <para>标签为 <see cref="EncodedValueType.Enum"/>、负载为索引的编码值。</para></returns>
    public static EncodedValue Enum(int index) => new(EncodedValueType.Enum, index);

    /// <summary>
    /// Creates a value array (VALUE_ARRAY).
    /// <para>构造值数组（VALUE_ARRAY）。</para>
    /// </summary>
    /// <param name="items">The element values; the produced array is stored as the payload without copying. <para>元素值；生成的数组直接作为负载保存，不做复制。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Array"/>. <para>标签为 <see cref="EncodedValueType.Array"/> 的编码值。</para></returns>
    public static EncodedValue Array(params EncodedValue[] items) => new(EncodedValueType.Array, items);

    /// <summary>
    /// Creates an annotation value (VALUE_ANNOTATION).
    /// <para>构造注解值（VALUE_ANNOTATION）。</para>
    /// </summary>
    /// <param name="item">The nested annotation item. <para>嵌套的注解项。</para></param>
    /// <returns>An encoded value tagged <see cref="EncodedValueType.Annotation"/> with the item as payload. <para>标签为 <see cref="EncodedValueType.Annotation"/>、负载为该注解项的编码值。</para></returns>
    public static EncodedValue Annotation(AnnotationItem item) => new(EncodedValueType.Annotation, item);

    /// <summary>
    /// Convenience accessor: boolean value.
    /// <para>便捷取值：布尔值。</para>
    /// </summary>
    /// <returns><see langword="true"/> only when the payload is a <c>bool</c> holding <see langword="true"/>; every other payload yields <see langword="false"/>. <para>仅当负载为值为 <see langword="true"/> 的 <c>bool</c> 时返回 <see langword="true"/>；其他负载一律返回 <see langword="false"/>。</para></returns>
    public bool AsBoolean() => Value is bool b && b;

    /// <summary>
    /// Convenience accessor: integral value (Int/Short/Byte/Char unified to int).
    /// <para>便捷取值：整数（Int/Short/Byte/Char 统一转 int）。</para>
    /// </summary>
    /// <returns>The payload widened to <c>int</c> for <c>int</c>, <c>short</c>, <c>byte</c> or <c>char</c>; <c>0</c> for any other payload, including <see langword="null"/>. <para>负载为 <c>int</c>、<c>short</c>、<c>byte</c> 或 <c>char</c> 时加宽为 <c>int</c>；其他负载（含 <see langword="null"/>）返回 <c>0</c>。</para></returns>
    public int AsInt() => Value switch
    {
        int i => i,
        short s => s,
        byte b => b,
        char c => c,
        _ => 0
    };

    /// <summary>
    /// Convenience accessor: long value.
    /// <para>便捷取值：长整数。</para>
    /// </summary>
    /// <returns>The payload widened to <c>long</c> for <c>long</c>, <c>int</c>, <c>short</c> or <c>byte</c>; <c>0</c> for any other payload, including <c>char</c> and <see langword="null"/>. <para>负载为 <c>long</c>、<c>int</c>、<c>short</c> 或 <c>byte</c> 时加宽为 <c>long</c>；其他负载（包括 <c>char</c> 与 <see langword="null"/>）返回 <c>0</c>。</para></returns>
    public long AsLong() => Value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        _ => 0
    };

    /// <summary>
    /// Convenience accessor: index value (String/Type/Field/Method/Enum etc.).
    /// <para>便捷取值：索引值（String/Type/Field/Method/Enum 等）。</para>
    /// </summary>
    /// <returns>The payload when it is an <c>int</c> index; <c>-1</c> for any other payload. <para>负载为 <c>int</c> 索引时原样返回；其他负载返回 <c>-1</c>。</para></returns>
    public int AsIndex() => Value is int i ? i : -1;

    /// <summary>
    /// Convenience accessor: value array.
    /// <para>便捷取值：值数组。</para>
    /// </summary>
    /// <returns>The payload when it is an <see cref="EncodedValue"/> array; <see langword="null"/> otherwise. <para>负载为 <see cref="EncodedValue"/> 数组时原样返回；否则返回 <see langword="null"/>。</para></returns>
    public EncodedValue[]? AsArray() => Value as EncodedValue[];
}