using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Model;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;

public sealed class DexFile
{
    /// <summary>
    /// Creates an empty DEX model (default version 035) for programmatic construction and tests.
    /// <para>创建空白 DEX 模型（默认版本 035），用于程序化构造与测试。</para>
    /// </summary>
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

    public DexHeader Header { get; }

    public List<string> Strings { get; } = [];

    public List<string> Types { get; } = [];

    public List<ProtoDefinition> Protos { get; } = [];

    public List<FieldId> Fields { get; } = [];

    public List<MethodId> Methods { get; } = [];

    public List<ClassDefinition> Classes { get; } = [];

    /// <summary>Gets the DEX 038+ method handles. <para>获取 DEX 038 及以上版本的方法句柄。</para></summary>
    public List<MethodHandleDefinition> MethodHandles { get; } = [];

    /// <summary>Gets the DEX 038+ call-site argument arrays. <para>获取 DEX 038 及以上版本的调用点参数数组。</para></summary>
    public List<List<EncodedValue>> CallSites { get; } = [];

    /// <summary>
    /// Loads a DEX from a file.
    /// <para>从文件加载 DEX。</para>
    /// </summary>
    public static DexFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new DexException($"DEX file not found: {path}");
        using var stream = File.OpenRead(path);
        return Load(AXml.AndroidMetadataReader.Read(stream));
    }

    /// <summary>
    /// Loads a DEX from a byte array.
    /// <para>从字节数组加载 DEX。</para>
    /// </summary>
    public static DexFile Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return DexReader.Parse(data);
    }

    /// <summary>
    /// Serializes the current model into DEX bytes (rebuilds all tables, the map list, and checksum/signature).
    /// <para>把当前模型序列化为 DEX 字节（重建所有表、map_list 与校验和/签名）。</para>
    /// </summary>
    public byte[] ToArray() => DexWriter.Write(this);

    /// <summary>
    /// Rebuilds the current model to the specified path.
    /// <para>把当前模型重建写出到指定路径。</para>
    /// </summary>
    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = ToArray();
        SharedKernel.FileExtraction.WriteAtomic(path, stream => stream.Write(bytes));
    }

    /// <summary>
    /// Produces a description summary (version and table statistics).
    /// <para>生成文件描述摘要（版本与各表统计）。</para>
    /// </summary>
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
    /// Gets a string by index.
    /// <para>按索引获取字符串。</para>
    /// </summary>
    public string GetString(int index) => (uint)index < Strings.Count ? Strings[index] : string.Empty;

    /// <summary>
    /// Gets a type descriptor by index.
    /// <para>按索引获取类型描述符。</para>
    /// </summary>
    public string GetType(int index) => (uint)index < Types.Count ? Types[index] : "?";

    /// <summary>
    /// Gets a field reference by index.
    /// <para>按索引获取字段引用。</para>
    /// </summary>
    public FieldId? GetField(int index) => (uint)index < Fields.Count ? Fields[index] : null;

    /// <summary>
    /// Gets a method reference by index.
    /// <para>按索引获取方法引用。</para>
    /// </summary>
    public MethodId? GetMethod(int index) => (uint)index < Methods.Count ? Methods[index] : null;

    /// <summary>
    /// Gets a prototype by index.
    /// <para>按索引获取原型。</para>
    /// </summary>
    public ProtoDefinition? GetProto(int index) => (uint)index < Protos.Count ? Protos[index] : null;

    /// <summary>
    /// Formats a type descriptor into a readable form (e.g. "Lcom/x/Foo;" → "com.x.Foo").
    /// <para>把类型描述符格式化为可读形式（如 "Lcom/x/Foo;" → "com.x.Foo"）。</para>
    /// </summary>
    public string FormatType(int index)
    {
        var descriptor = GetType(index);
        return FormatDescriptor(descriptor);
    }

    /// <summary>
    /// Formats a type descriptor string into a readable form.
    /// <para>把类型描述符字符串格式化为可读形式。</para>
    /// </summary>
    public static string FormatDescriptor(string descriptor)
    {
        if (string.IsNullOrEmpty(descriptor)) return "?";
        var arrayDepth = 0;
        while (arrayDepth < descriptor.Length && descriptor[arrayDepth] == '[') arrayDepth++;
        if (arrayDepth == descriptor.Length) return descriptor;

        var bodyStart = arrayDepth;
        var body = descriptor[bodyStart];

        string name;
        if (body == 'L' && descriptor.Length - bodyStart > 2)
        {


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
    /// Formats a field reference as "owner-&gt;name:type".
    /// <para>把字段引用格式化为 "所有者-&gt;名称:类型" 形式。</para>
    /// </summary>
    public string FormatField(FieldId field)
    {
        var owner = FormatType(field.ClassIndex);
        var name = GetString(field.NameIndex);
        var type = FormatType(field.TypeIndex);
        return $"{owner}->{name}:{type}";
    }

    /// <summary>
    /// Formats a method reference as "owner-&gt;name(params)return".
    /// <para>把方法引用格式化为 "所有者-&gt;名称(参数)返回" 形式。</para>
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