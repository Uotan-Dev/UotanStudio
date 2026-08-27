using System.Text;
using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex;

/// <summary>
/// <para>DEX 文件对象模型：头部 + 字符串/类型/原型/字段/方法表 + 类定义，支持解析、重建写出与描述。</para>
/// DEX file object model: header + string/type/proto/field/method tables + class definitions, supporting
/// parsing, rebuild-write and description.
/// </summary>
public sealed class DexFile
{
    /// <summary>
    /// <para>创建空白 DEX 模型（默认版本 035），用于程序化构造与测试。</para>
    /// Creates an empty DEX model (default version 035) for programmatic construction and tests.
    /// </summary>
    /// <param name="version">DEX 版本号（如 "035"）。<para>DEX version (e.g. "035").</para></param>
    public DexFile(string version = "035")
    {
        Header = new DexHeader
        {
            Version = version,
            Magic = Encoding.ASCII.GetBytes("dex\n" + version + "\0")
        };
    }

    internal DexFile(DexHeader header)
    {
        Header = header;
    }

    /// <summary>
    /// <para>获取文件头部。</para>
    /// Gets the file header.
    /// </summary>
    public DexHeader Header { get; }

    /// <summary>
    /// <para>字符串表（string_ids 顺序）。</para>
    /// String table (string_ids order).
    /// </summary>
    public List<string> Strings { get; } = [];

    /// <summary>
    /// <para>类型表（type_ids 顺序，元素为描述符如 "Lcom/x/Foo;"）。</para>
    /// Type table (type_ids order; elements are descriptors like "Lcom/x/Foo;").
    /// </summary>
    public List<string> Types { get; } = [];

    /// <summary>
    /// <para>原型表（proto_ids 顺序）。</para>
    /// Prototype table (proto_ids order).
    /// </summary>
    public List<ProtoDefinition> Protos { get; } = [];

    /// <summary>
    /// <para>字段表（field_ids 顺序）。</para>
    /// Field table (field_ids order).
    /// </summary>
    public List<FieldId> Fields { get; } = [];

    /// <summary>
    /// <para>方法表（method_ids 顺序）。</para>
    /// Method table (method_ids order).
    /// </summary>
    public List<MethodId> Methods { get; } = [];

    /// <summary>
    /// <para>类定义表（class_defs 顺序）。</para>
    /// Class definition table (class_defs order).
    /// </summary>
    public List<ClassDefinition> Classes { get; } = [];

    /// <summary>
    /// <para>从文件加载 DEX。</para>
    /// Loads a DEX from a file.
    /// </summary>
    /// <param name="path">DEX 文件路径。<para>DEX file path.</para></param>
    /// <returns>解析后的模型。<para>The parsed model.</para></returns>
    public static DexFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new DexException($"DEX file not found: {path}");
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>
    /// <para>从字节数组加载 DEX。</para>
    /// Loads a DEX from a byte array.
    /// </summary>
    /// <param name="data">DEX 字节。<para>DEX bytes.</para></param>
    /// <returns>解析后的模型。<para>The parsed model.</para></returns>
    public static DexFile Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return DexReader.Parse(data);
    }

    /// <summary>
    /// <para>把当前模型序列化为 DEX 字节（重建所有表、map_list 与校验和/签名）。</para>
    /// Serializes the current model into DEX bytes (rebuilds all tables, the map list, and checksum/signature).
    /// </summary>
    /// <returns>DEX 字节。<para>DEX bytes.</para></returns>
    public byte[] ToArray() => DexWriter.Write(this);

    /// <summary>
    /// <para>把当前模型重建写出到指定路径。</para>
    /// Rebuilds the current model to the specified path.
    /// </summary>
    /// <param name="path">输出路径。<para>Output path.</para></param>
    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, ToArray());
    }

    /// <summary>
    /// <para>生成文件描述摘要（版本与各表统计）。</para>
    /// Produces a description summary (version and table statistics).
    /// </summary>
    /// <returns>描述文本。<para>Description text.</para></returns>
    public string Describe()
    {
        var methodCount = 0;
        var fieldCount = 0;
        foreach (var cls in Classes)
        {
            methodCount += cls.DirectMethods.Count + cls.VirtualMethods.Count;
            fieldCount += cls.StaticFields.Count + cls.InstanceFields.Count;
        }

        var sb = new StringBuilder();
        sb.Append("DEX v").Append(Header.Version);
        sb.Append(" | strings: ").Append(Strings.Count);
        sb.Append(" | types: ").Append(Types.Count);
        sb.Append(" | protos: ").Append(Protos.Count);
        sb.Append(" | fields: ").Append(Fields.Count);
        sb.Append(" | methods: ").Append(Methods.Count);
        sb.Append(" | classes: ").Append(Classes.Count);
        sb.Append(" | declared methods: ").Append(methodCount);
        sb.Append(" | declared fields: ").Append(fieldCount);
        if (Classes.Count > 0)
        {
            sb.AppendLine();
            foreach (var cls in Classes.Take(16))
            {
                sb.Append("  ").Append(FormatType(cls.TypeIndex));
                if (cls.DirectMethods.Count + cls.VirtualMethods.Count > 0)
                {
                    sb.Append(" (").Append(cls.DirectMethods.Count + cls.VirtualMethods.Count).Append(" methods)");
                }
                sb.AppendLine();
            }
            if (Classes.Count > 16) sb.Append("  ...").Append(Classes.Count - 16).AppendLine(" more");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// <para>按索引获取字符串。</para>
    /// Gets a string by index.
    /// </summary>
    public string GetString(int index) => (uint)index < Strings.Count ? Strings[index] : string.Empty;

    /// <summary>
    /// <para>按索引获取类型描述符。</para>
    /// Gets a type descriptor by index.
    /// </summary>
    public string GetType(int index) => (uint)index < Types.Count ? Types[index] : "?";

    /// <summary>
    /// <para>按索引获取字段引用。</para>
    /// Gets a field reference by index.
    /// </summary>
    public FieldId? GetField(int index) => (uint)index < Fields.Count ? Fields[index] : null;

    /// <summary>
    /// <para>按索引获取方法引用。</para>
    /// Gets a method reference by index.
    /// </summary>
    public MethodId? GetMethod(int index) => (uint)index < Methods.Count ? Methods[index] : null;

    /// <summary>
    /// <para>按索引获取原型。</para>
    /// Gets a prototype by index.
    /// </summary>
    public ProtoDefinition? GetProto(int index) => (uint)index < Protos.Count ? Protos[index] : null;

    /// <summary>
    /// <para>把类型描述符格式化为可读形式（如 "Lcom/x/Foo;" → "com.x.Foo"）。</para>
    /// Formats a type descriptor into a readable form (e.g. "Lcom/x/Foo;" → "com.x.Foo").
    /// </summary>
    /// <param name="index">类型索引。<para>Type index.</para></param>
    public string FormatType(int index)
    {
        var descriptor = GetType(index);
        return FormatDescriptor(descriptor);
    }

    /// <summary>
    /// <para>把类型描述符字符串格式化为可读形式。</para>
    /// Formats a type descriptor string into a readable form.
    /// </summary>
    /// <param name="descriptor">描述符。<para>The descriptor.</para></param>
    public static string FormatDescriptor(string descriptor)
    {
        if (string.IsNullOrEmpty(descriptor)) return "?";
        var arrayDepth = 0;
        while (arrayDepth < descriptor.Length && descriptor[arrayDepth] == '[') arrayDepth++;
        if (arrayDepth == descriptor.Length) return descriptor; // 全为 '[' 的退化输入

        var bodyStart = arrayDepth;
        var body = descriptor[bodyStart];

        string name;
        if (body == 'L' && descriptor.Length - bodyStart > 2)
        {
            // 类描述符：单次分配构建（去首 'L' 尾 ';'，'/' 替换为 '.'），避免切片与 Replace 的多次分配。
            // Class descriptor: build in one allocation (strip the leading 'L' and trailing ';',
            // replace '/' with '.') to avoid the slice/Replace allocations.
            var innerStart = bodyStart + 1;
            var innerLen = descriptor.Length - bodyStart - 2;
            name = string.Create(innerLen, (descriptor, innerStart), static (span, state) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    var c = state.descriptor[state.innerStart + i];
                    span[i] = c == '/' ? '.' : c;
                }
            });
        }
        else
        {
            name = body switch
            {
                'V' => "void",
                'Z' => "boolean",
                'B' => "byte",
                'S' => "short",
                'C' => "char",
                'I' => "int",
                'J' => "long",
                'F' => "float",
                'D' => "double",
                _ => descriptor[bodyStart..]
            };
        }
        return arrayDepth == 0 ? name : name + new string('[', arrayDepth);
    }

    /// <summary>
    /// <para>把字段引用格式化为 "所有者->名称:类型" 形式。</para>
    /// Formats a field reference as "owner->name:type".
    /// </summary>
    public string FormatField(FieldId field)
    {
        var owner = FormatType(field.ClassIndex);
        var name = GetString(field.NameIndex);
        var type = FormatType(field.TypeIndex);
        return $"{owner}->{name}:{type}";
    }

    /// <summary>
    /// <para>把方法引用格式化为 "所有者->名称(参数)返回" 形式。</para>
    /// Formats a method reference as "owner->name(params)return".
    /// </summary>
    public string FormatMethod(MethodId method)
    {
        var owner = FormatType(method.ClassIndex);
        var name = GetString(method.NameIndex);
        var proto = GetProto(method.ProtoIndex);
        var sb = new StringBuilder();
        sb.Append(owner).Append("->").Append(name).Append('(');
        if (proto != null)
        {
            for (var i = 0; i < proto.ParameterTypeIndices.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(FormatType(proto.ParameterTypeIndices[i]));
            }
        }
        sb.Append(')');
        sb.Append(proto != null ? FormatType(proto.ReturnTypeIndex) : "?");
        return sb.ToString();
    }
}