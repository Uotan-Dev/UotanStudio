namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

public enum EncodedValueType : byte
{
    Byte = 0x00,
    Short = 0x02,
    Char = 0x03,
    Int = 0x04,
    Long = 0x06,
    Float = 0x10,
    Double = 0x11,
    MethodType = 0x15,
    MethodHandle = 0x16,
    String = 0x17,
    Type = 0x18,
    Field = 0x19,
    Method = 0x1a,
    Enum = 0x1b,
    Array = 0x1c,
    Annotation = 0x1d,
    Null = 0x1e,
    Boolean = 0x1f
}

public sealed class EncodedValue
{
    public EncodedValueType Type { get; init; }

    public object? Value { get; init; }

    /// <summary>
    /// Creates an encoded value.
    /// <para>构造编码值。</para>
    /// </summary>
    public EncodedValue(EncodedValueType type, object? value)
    {
        Type = type;
        Value = value;
    }

    /// <summary>
    /// Creates a null value (VALUE_NULL).
    /// <para>构造空值（VALUE_NULL）。</para>
    /// </summary>
    public static EncodedValue Null() => new(EncodedValueType.Null, null);

    /// <summary>
    /// Creates a boolean value (VALUE_BOOLEAN).
    /// <para>构造布尔值（VALUE_BOOLEAN）。</para>
    /// </summary>
    public static EncodedValue Boolean(bool value) => new(EncodedValueType.Boolean, value);

    /// <summary>
    /// Creates an int value (VALUE_INT).
    /// <para>构造整数值（VALUE_INT）。</para>
    /// </summary>
    public static EncodedValue Int(int value) => new(EncodedValueType.Int, value);

    /// <summary>
    /// Creates a long value (VALUE_LONG).
    /// <para>构造长整数值（VALUE_LONG）。</para>
    /// </summary>
    public static EncodedValue Long(long value) => new(EncodedValueType.Long, value);

    /// <summary>
    /// Creates a short value (VALUE_SHORT).
    /// <para>构造短整数值（VALUE_SHORT）。</para>
    /// </summary>
    public static EncodedValue Short(short value) => new(EncodedValueType.Short, value);

    /// <summary>
    /// Creates a byte value (VALUE_BYTE).
    /// <para>构造字节值（VALUE_BYTE）。</para>
    /// </summary>
    public static EncodedValue Byte(byte value) => new(EncodedValueType.Byte, value);

    /// <summary>
    /// Creates a char value (VALUE_CHAR).
    /// <para>构造字符值（VALUE_CHAR）。</para>
    /// </summary>
    public static EncodedValue Char(char value) => new(EncodedValueType.Char, value);

    /// <summary>
    /// Creates a float value (VALUE_FLOAT).
    /// <para>构造浮点值（VALUE_FLOAT）。</para>
    /// </summary>
    public static EncodedValue Float(float value) => new(EncodedValueType.Float, value);

    /// <summary>
    /// Creates a double value (VALUE_DOUBLE).
    /// <para>构造双精度值（VALUE_DOUBLE）。</para>
    /// </summary>
    public static EncodedValue Double(double value) => new(EncodedValueType.Double, value);

    /// <summary>
    /// Creates a string-index value (VALUE_STRING).
    /// <para>构造字符串索引值（VALUE_STRING）。</para>
    /// </summary>
    public static EncodedValue String(int index) => new(EncodedValueType.String, index);

    /// <summary>
    /// Creates a type-index value (VALUE_TYPE).
    /// <para>构造类型索引值（VALUE_TYPE）。</para>
    /// </summary>
    public static EncodedValue TypeRef(int index) => new(EncodedValueType.Type, index);

    /// <summary>
    /// Creates a field-index value (VALUE_FIELD).
    /// <para>构造字段索引值（VALUE_FIELD）。</para>
    /// </summary>
    public static EncodedValue Field(int index) => new(EncodedValueType.Field, index);

    /// <summary>
    /// Creates a method-index value (VALUE_METHOD).
    /// <para>构造方法索引值（VALUE_METHOD）。</para>
    /// </summary>
    public static EncodedValue Method(int index) => new(EncodedValueType.Method, index);

    /// <summary>
    /// Creates an enum-index value (VALUE_ENUM; a field_ids index).
    /// <para>构造枚举索引值（VALUE_ENUM，值为 field_ids 索引）。</para>
    /// </summary>
    public static EncodedValue Enum(int index) => new(EncodedValueType.Enum, index);

    /// <summary>
    /// Creates a value array (VALUE_ARRAY).
    /// <para>构造值数组（VALUE_ARRAY）。</para>
    /// </summary>
    public static EncodedValue Array(params EncodedValue[] items) => new(EncodedValueType.Array, items);

    /// <summary>
    /// Creates an annotation value (VALUE_ANNOTATION).
    /// <para>构造注解值（VALUE_ANNOTATION）。</para>
    /// </summary>
    public static EncodedValue Annotation(AnnotationItem item) => new(EncodedValueType.Annotation, item);

    /// <summary>
    /// Convenience accessor: boolean value.
    /// <para>便捷取值：布尔值。</para>
    /// </summary>
    public bool AsBoolean() => Value is bool b && b;

    /// <summary>
    /// Convenience accessor: integral value (Int/Short/Byte/Char unified to int).
    /// <para>便捷取值：整数（Int/Short/Byte/Char 统一转 int）。</para>
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
    /// Convenience accessor: long value.
    /// <para>便捷取值：长整数。</para>
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
    /// Convenience accessor: index value (String/Type/Field/Method/Enum etc.).
    /// <para>便捷取值：索引值（String/Type/Field/Method/Enum 等）。</para>
    /// </summary>
    public int AsIndex() => Value is int i ? i : -1;

    /// <summary>
    /// Convenience accessor: value array.
    /// <para>便捷取值：值数组。</para>
    /// </summary>
    public EncodedValue[]? AsArray() => Value as EncodedValue[];
}