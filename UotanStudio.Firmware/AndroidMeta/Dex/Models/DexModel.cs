namespace UotanStudio.Firmware.AndroidMeta.Dex.Models;

/// <summary>
/// Decoded proto_ids entry: the shorty string index plus return and parameter type indices that
/// describe one method signature.
/// <para>解码后的 proto_ids 条目：描述单个方法签名的 shorty 字符串索引、返回类型与参数类型索引。</para>
/// </summary>
public sealed class ProtoDefinition
{
    /// <summary>String table slot holding this prototype's shorty signature. <para>存放本原型 shorty 签名字符串的字符串表槽位。</para></summary>
    /// <value>Zero-based <c>string_ids</c> index, stored verbatim from the on-disk 32-bit <c>shorty_idx</c> field.
    /// <para>从零开始的 <c>string_ids</c> 索引，原样取自磁盘上的 32 位 <c>shorty_idx</c> 字段。</para></value>
    public int ShortyIndex { get; set; }

    /// <summary>Type table slot of the returned value. <para>返回值在类型表中的槽位。</para></summary>
    /// <value>Zero-based <c>type_ids</c> index, stored verbatim from the on-disk 32-bit <c>return_type_idx</c> field.
    /// <para>从零开始的 <c>type_ids</c> 索引，原样取自磁盘上的 32 位 <c>return_type_idx</c> 字段。</para></value>
    public int ReturnTypeIndex { get; set; }

    /// <summary>Type table slots of the parameters in declaration order. <para>按声明顺序排列的参数类型表槽位。</para></summary>
    /// <value>Zero-based <c>type_ids</c> indices; the default empty array means the method takes no parameters,
    /// matching a zero <c>parameters_off</c> field on disk.
    /// <para>从零开始的 <c>type_ids</c> 索引；默认空数组表示方法没有参数，对应磁盘上的 <c>parameters_off</c> 字段为零。</para></value>
    public int[] ParameterTypeIndices { get; set; } = [];

    /// <summary>
    /// Computes the shorty string (e.g. "VIL") for writing.
    /// <para>计算 shorty 字符串（如 "VIL"），供写出时使用。</para>
    /// </summary>
    /// <param name="dex">
    /// File model whose <c>Types</c> table resolves <see cref="ReturnTypeIndex"/> and every entry of
    /// <see cref="ParameterTypeIndices"/>.
    /// <para>其 <c>Types</c> 表用于解析 <see cref="ReturnTypeIndex"/> 与 <see cref="ParameterTypeIndices"/> 各条目的文件模型。</para>
    /// </param>
    /// <returns>The return-type letter followed by one letter per parameter: array and object descriptors
    /// collapse to <c>L</c>, primitive descriptors keep their leading letter.
    /// <para>返回类型字母后接每个参数一个字母：数组与对象描述符归并为 <c>L</c>，基本类型描述符保留首字母。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">An index does not address an entry of <c>dex.Types</c>.
    /// <para>某个索引无法命中 <c>dex.Types</c> 中的条目。</para></exception>
    /// <exception cref="IndexOutOfRangeException">A resolved descriptor is the empty string.
    /// <para>解析出的描述符为空字符串。</para></exception>
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
/// Decoded field_ids entry: the class, declared type and simple name indices identifying one field.
/// <para>解码后的 field_ids 条目：标识单个字段的类、声明类型与简单名称索引。</para>
/// </summary>
public sealed class FieldId
{
    /// <summary>Class that declares the field. <para>声明该字段的类。</para></summary>
    /// <value>Zero-based <c>type_ids</c> index, read from the 16-bit on-disk <c>class_idx</c> slot.
    /// <para>从零开始的 <c>type_ids</c> 索引，读自 16 位的磁盘 <c>class_idx</c> 槽位。</para></value>
    public int ClassIndex { get; set; }

    /// <summary>Declared type of the field. <para>字段的声明类型。</para></summary>
    /// <value>Zero-based <c>type_ids</c> index, read from the 16-bit on-disk <c>type_idx</c> slot.
    /// <para>从零开始的 <c>type_ids</c> 索引，读自 16 位的磁盘 <c>type_idx</c> 槽位。</para></value>
    public int TypeIndex { get; set; }

    /// <summary>Simple name of the field. <para>字段的简单名称。</para></summary>
    /// <value>Zero-based <c>string_ids</c> index, read from the 32-bit on-disk <c>name_idx</c> slot.
    /// <para>从零开始的 <c>string_ids</c> 索引，读自 32 位的磁盘 <c>name_idx</c> 槽位。</para></value>
    public int NameIndex { get; set; }
}

/// <summary>
/// Decoded method_ids entry: the declaring class, prototype and simple name indices identifying one method.
/// <para>解码后的 method_ids 条目：标识单个方法的声明类、原型与简单名称索引。</para>
/// </summary>
public sealed class MethodId
{
    /// <summary>Class that declares the method. <para>声明该方法的类。</para></summary>
    /// <value>Zero-based <c>type_ids</c> index, read from the 16-bit on-disk <c>class_idx</c> slot.
    /// <para>从零开始的 <c>type_ids</c> 索引，读自 16 位的磁盘 <c>class_idx</c> 槽位。</para></value>
    public int ClassIndex { get; set; }

    /// <summary>Signature the method is declared with. <para>方法声明所使用的签名。</para></summary>
    /// <value>Zero-based <c>proto_ids</c> index, read from the 16-bit on-disk <c>proto_idx</c> slot.
    /// <para>从零开始的 <c>proto_ids</c> 索引，读自 16 位的磁盘 <c>proto_idx</c> 槽位。</para></value>
    public int ProtoIndex { get; set; }

    /// <summary>Simple name of the method. <para>方法的简单名称。</para></summary>
    /// <value>Zero-based <c>string_ids</c> index, read from the 32-bit on-disk <c>name_idx</c> slot.
    /// <para>从零开始的 <c>string_ids</c> 索引，读自 32 位的磁盘 <c>name_idx</c> 槽位。</para></value>
    public int NameIndex { get; set; }
}

/// <summary>Describes a DEX method handle. <para>描述 DEX 方法句柄。</para></summary>
public sealed class MethodHandleDefinition
{
    /// <summary>Gets or sets the handle kind (0 through 8). <para>获取或设置句柄种类（0 至 8）。</para></summary>
    /// <value>Kind code stored verbatim from the 16-bit <c>method_handle_type</c> field; reading a file rejects values above <c>8</c>.
    /// <para>原样存储自 16 位 <c>method_handle_type</c> 字段的种类码；读取文件时会拒绝大于 <c>8</c> 的值。</para></value>
    public ushort Kind { get; set; }

    /// <summary>Gets or sets the referenced field or method index. <para>获取或设置引用的字段或方法索引。</para></summary>
    /// <value>Zero-based <c>field_ids</c> index when <see cref="Kind"/> is 0 through 3, otherwise a zero-based
    /// <c>method_ids</c> index; reading a file rejects out-of-range values.
    /// <para><see cref="Kind"/> 为 0 至 3 时是零基 <c>field_ids</c> 索引，否则是零基 <c>method_ids</c> 索引；读取文件时会拒绝越界值。</para></value>
    public ushort MemberIndex { get; set; }
}

/// <summary>
/// Encoded static or instance field entry from a class_data_item, with the index delta already
/// resolved to an absolute table position.
/// <para>来自 class_data_item 的编码静态/实例字段条目，索引差值已解析为绝对表位置。</para>
/// </summary>
public sealed class EncodedField
{
    /// <summary>Field this entry describes. <para>本条目描述的字段。</para></summary>
    /// <value>Zero-based <c>field_ids</c> index after accumulating the ULEB128 delta chain.
    /// <para>累加 ULEB128 差值链后的零基 <c>field_ids</c> 索引。</para></value>
    public int FieldIndex { get; set; }

    /// <summary>Access modifiers carried by the encoded field. <para>编码字段携带的访问修饰符。</para></summary>
    /// <value>ULEB128-decoded Java access flag bits for fields; the reader stores them without filtering.
    /// <para>ULEB128 解码得到的字段 Java 访问标志位；读取器不做过滤地存储。</para></value>
    public uint AccessFlags { get; set; }
}

/// <summary>
/// Encoded direct or virtual method entry from a class_data_item, with the index delta already
/// resolved to an absolute table position.
/// <para>来自 class_data_item 的编码直接/虚方法条目，索引差值已解析为绝对表位置。</para>
/// </summary>
public sealed class EncodedMethod
{
    /// <summary>Method this entry describes. <para>本条目描述的方法。</para></summary>
    /// <value>Zero-based <c>method_ids</c> index after accumulating the ULEB128 delta chain.
    /// <para>累加 ULEB128 差值链后的零基 <c>method_ids</c> 索引。</para></value>
    public int MethodIndex { get; set; }

    /// <summary>Access modifiers carried by the encoded method. <para>编码方法携带的访问修饰符。</para></summary>
    /// <value>ULEB128-decoded Java access flag bits for methods; the reader stores them without filtering.
    /// <para>ULEB128 解码得到的方法 Java 访问标志位；读取器不做过滤地存储。</para></value>
    public uint AccessFlags { get; set; }

    /// <summary>Bytecode body of the method. <para>方法的字节码主体。</para></summary>
    /// <value><see langword="null"/> when the on-disk <c>code_off</c> is 0, as stored for abstract and native methods;
    /// otherwise the decoded code item.
    /// <para>磁盘上 <c>code_off</c> 为 0（抽象方法与本地方法的存储方式）时为 <see langword="null"/>；否则为解码出的代码项。</para></value>
    public CodeItem? Code { get; set; }
}