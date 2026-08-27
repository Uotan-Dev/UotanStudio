namespace UotanStudio.Firmware.Dex.Model;

/// <summary>
/// <para>方法原型（proto_id_item）：短描述、返回类型与参数类型索引。</para>
/// Method prototype (proto_id_item): shorty, return type and parameter type indices.
/// </summary>
public sealed class ProtoDefinition
{
    /// <summary>shorty 字符串索引。<para>Shorty string index.</para></summary>
    public int ShortyIndex { get; set; }

    /// <summary>返回类型索引（type_ids）。<para>Return type index (type_ids).</para></summary>
    public int ReturnTypeIndex { get; set; }

    /// <summary>参数类型索引数组（type_ids）。<para>Parameter type index array (type_ids).</para></summary>
    public int[] ParameterTypeIndices { get; set; } = [];

    /// <summary>
    /// <para>计算 shorty 字符串（如 "VIL"），供写出时使用。</para>
    /// Computes the shorty string (e.g. "VIL") for writing.
    /// </summary>
    /// <param name="dex">所属 DEX 文件（用于解析类型描述符）。<para>Owning DEX file (for resolving descriptors).</para></param>
    public string ComputeShorty(DexFile dex)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(ShortyChar(dex.Types[ReturnTypeIndex]));
        foreach (var p in ParameterTypeIndices)
        {
            sb.Append(ShortyChar(dex.Types[p]));
        }
        return sb.ToString();
    }

    private static char ShortyChar(string descriptor) => descriptor[0] switch
    {
        '[' => 'L',
        'L' => 'L',
        var c => c
    };
}

/// <summary>
/// <para>字段引用（field_id_item）：所属类、类型与名称索引。</para>
/// Field reference (field_id_item): class, type and name indices.
/// </summary>
public sealed class FieldId
{
    /// <summary>所属类类型索引。<para>Class type index.</para></summary>
    public int ClassIndex { get; set; }

    /// <summary>字段类型索引。<para>Field type index.</para></summary>
    public int TypeIndex { get; set; }

    /// <summary>字段名称字符串索引。<para>Field name string index.</para></summary>
    public int NameIndex { get; set; }
}

/// <summary>
/// <para>方法引用（method_id_item）：所属类、原型与名称索引。</para>
/// Method reference (method_id_item): class, prototype and name indices.
/// </summary>
public sealed class MethodId
{
    /// <summary>所属类类型索引。<para>Class type index.</para></summary>
    public int ClassIndex { get; set; }

    /// <summary>原型索引（proto_ids）。<para>Prototype index (proto_ids).</para></summary>
    public int ProtoIndex { get; set; }

    /// <summary>方法名称字符串索引。<para>Method name string index.</para></summary>
    public int NameIndex { get; set; }
}

/// <summary>
/// <para>类数据中的编码字段（encoded_field）：字段引用索引 + 访问标志。</para>
/// Encoded field in class data (encoded_field): field reference index + access flags.
/// </summary>
public sealed class EncodedField
{
    /// <summary>字段引用索引（field_ids）。<para>Field reference index (field_ids).</para></summary>
    public int FieldIndex { get; set; }

    /// <summary>访问标志。<para>Access flags.</para></summary>
    public uint AccessFlags { get; set; }
}

/// <summary>
/// <para>类数据中的编码方法（encoded_method）：方法引用索引 + 访问标志 + 可选代码。</para>
/// Encoded method in class data (encoded_method): method reference index + access flags + optional code.
/// </summary>
public sealed class EncodedMethod
{
    /// <summary>方法引用索引（method_ids）。<para>Method reference index (method_ids).</para></summary>
    public int MethodIndex { get; set; }

    /// <summary>访问标志。<para>Access flags.</para></summary>
    public uint AccessFlags { get; set; }

    /// <summary>代码项；无代码时为 <c>null</c>（native/abstract）。<para>Code item; <c>null</c> when no code (native/abstract).</para></summary>
    public CodeItem? Code { get; set; }
}