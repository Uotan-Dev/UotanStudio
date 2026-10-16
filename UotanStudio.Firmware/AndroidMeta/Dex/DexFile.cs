using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex;

/// <summary>
/// In-memory model of a Dalvik Executable: header, ID tables (strings, types, protos, fields,
/// methods, class definitions) and the DEX 038+ method-handle and call-site tables. Instances
/// built from untrusted bytes via <see cref="Load(string)"/> or <see cref="Load(byte[])"/> hold
/// only structures whose offsets and sizes were range-checked during parsing.
/// <para>Dalvik 可执行文件的内存模型：头部与各 ID 表（字符串、类型、原型、字段、方法、类定义），
/// 以及 DEX 038 及以上版本的方法句柄与调用点表。通过 <see cref="Load(string)"/> 或
/// <see cref="Load(byte[])"/> 从不可信字节构造的实例，仅包含解析时偏移与长度已通过范围检查的结构。</para>
/// </summary>
public sealed class DexFile
{
    /// <summary>
    /// Creates an empty DEX model (default version 035) for programmatic construction and tests.
    /// <para>创建空白 DEX 模型（默认版本 035），用于程序化构造与测试。</para>
    /// </summary>
    /// <param name="version">
    /// Version text embedded verbatim into the ASCII magic bytes; the constructor does not validate
    /// it, and parsing accepts only 035, 037, 038, 039 and 040.
    /// <para>原样写入 ASCII 魔数的版本文本；构造函数不校验它，解析仅接受 035、037、038、039 和 040。</para>
    /// </param>
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
    /// The 0x70-byte DEX header (magic, version, endian tag, table offsets and sizes) as parsed
    /// from the input or as defaulted for an empty model.
    /// <para>从输入解析或为空白模型默认生成的 0x70 字节 DEX 头部（魔数、版本、字节序标记、各表偏移与大小）。</para>
    /// </summary>
    /// <value>Read-only header snapshot backing the table lookups of this model. <para>本模型各表查找所依赖的只读头部快照。</para></value>
    public DexHeader Header { get; }

    /// <summary>
    /// string_ids table entries in file order, decoded from MUTF-8.
    /// <para>按文件顺序排列的 string_ids 表条目，已从 MUTF-8 解码。</para>
    /// </summary>
    /// <value>Mutable list; its indices are the string references used by types, fields, methods and encoded values. <para>可变列表；其索引即类型、字段、方法与编码值使用的字符串引用。</para></value>
    public List<string> Strings { get; } = [];

    /// <summary>
    /// type_ids table descriptors (e.g. "Ljava/lang/Object;") in file order; indices here are
    /// referenced by classes, protos, fields and methods.
    /// <para>按文件顺序排列的 type_ids 表描述符（如 "Ljava/lang/Object;"）；类、原型、字段与方法均按下标引用此表。</para>
    /// </summary>
    /// <value>Mutable list of raw JVM-style descriptors, not yet formatted. <para>原始 JVM 风格描述符的可变列表，尚未经格式化。</para></value>
    public List<string> Types { get; } = [];

    /// <summary>
    /// proto_ids table entries (shorty index, return type and parameter type indices) in file order.
    /// <para>按文件顺序排列的 proto_ids 表条目（shorty 索引、返回类型与参数类型索引）。</para>
    /// </summary>
    /// <value>Mutable list; an empty <c>ParameterTypeIndices</c> corresponds to a zero parameters-off field. <para>可变列表；<c>ParameterTypeIndices</c> 为空对应 parameters_off 字段为零。</para></value>
    public List<ProtoDefinition> Protos { get; } = [];

    /// <summary>
    /// field_ids table entries in file order.
    /// <para>按文件顺序排列的 field_ids 表条目。</para>
    /// </summary>
    /// <value>Mutable list of class/type/name index triples targeted by encoded field indices. <para>编码字段索引所指向的类/类型/名称三元组可变列表。</para></value>
    public List<FieldId> Fields { get; } = [];

    /// <summary>
    /// method_ids table entries in file order.
    /// <para>按文件顺序排列的 method_ids 表条目。</para>
    /// </summary>
    /// <value>Mutable list of class/proto/name index triples targeted by encoded method indices. <para>编码方法索引所指向的类/原型/名称三元组可变列表。</para></value>
    public List<MethodId> Methods { get; } = [];

    /// <summary>
    /// class_defs table entries in file order, each expanded with its class data, annotations and
    /// static values where present.
    /// <para>按文件顺序排列的 class_defs 表条目；存在时已展开各自的类数据、注解与静态值。</para>
    /// </summary>
    /// <value>Mutable list; superclass and source-file indices of <c>-1</c> denote the on-disk NO_INDEX value. <para>可变列表；父类与来源文件索引为 <c>-1</c> 表示磁盘上的 NO_INDEX 值。</para></value>
    public List<ClassDefinition> Classes { get; } = [];

    /// <summary>Gets the DEX 038+ method handles. <para>获取 DEX 038 及以上版本的方法句柄。</para></summary>
    /// <value>Method handles in map-list encounter order; empty before version 038. <para>按 map_list 出现顺序排列的方法句柄；038 之前版本为空。</para></value>
    public List<MethodHandleDefinition> MethodHandles { get; } = [];

    /// <summary>Gets the DEX 038+ call-site argument arrays. <para>获取 DEX 038 及以上版本的调用点参数数组。</para></summary>
    /// <value>One argument list per call site; the first three entries are the bootstrap method handle, method name string and prototype indices. <para>每个调用点一份参数列表；前三项依次为启动方法句柄、方法名字符串与原型索引。</para></value>
    public List<List<EncodedValue>> CallSites { get; } = [];

    /// <summary>
    /// Loads a DEX from a file.
    /// <para>从文件加载 DEX。</para>
    /// </summary>
    /// <param name="path">
    /// Existing file path; the whole file is buffered into memory under a 256 MiB cap before parsing.
    /// <para>已存在的文件路径；解析前整个文件会在 256 MiB 上限内完整读入内存。</para>
    /// </param>
    /// <returns>The parsed model, with every table range-checked against the actual file length.
    /// <para>解析后的模型；各表范围均已对照实际文件长度校验。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or white space. <para><paramref name="path"/> 为 null 或空白。</para></exception>
    /// <exception cref="DexException">The file does not exist, or its magic, version, endian tag, header size, file size, Adler-32 checksum or table ranges are invalid. <para>文件不存在，或魔数、版本、字节序标记、头部长度、文件大小、Adler-32 校验和与表范围校验失败。</para></exception>
    /// <exception cref="InvalidDataException">The file exceeds the 256 MiB metadata limit. <para>文件超过 256 MiB 元数据上限。</para></exception>
    /// <example>
    /// <code>
    /// var dex = DexFile.Load("classes.dex");
    /// Console.WriteLine(dex.FormatMethod(dex.GetMethod(0)!));
    /// </code>
    /// </example>
    public static DexFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new DexException($"DEX file not found: {path}");
        using var stream = File.OpenRead(path);
        return Load(IO.AndroidMetadataReader.Read(stream));
    }

    /// <summary>
    /// Loads a DEX from a byte array.
    /// <para>从字节数组加载 DEX。</para>
    /// </summary>
    /// <param name="data">
    /// Complete DEX image. The declared header file size must equal <c>data.Length</c>, and the declared
    /// Adler-32 checksum must match the bytes from offset 12 to the end; the input is
    /// treated as untrusted and every offset, count and length is bounds-checked before use.
    /// <para>完整 DEX 镜像。头部声明的文件大小必须等于 <c>data.Length</c>，且头部声明的 Adler-32 校验和必须与
    /// 偏移 12 至结尾的字节一致；输入按不可信处理，
    /// 所有偏移、数量与长度在使用前均经过边界检查。</para>
    /// </param>
    /// <returns>The parsed model. Only little-endian DEX (endian tag 0x12345678) versions 035, 037,
    /// 038, 039 and 040 are accepted. <para>解析后的模型。仅接受小端 DEX（字节序标记 0x12345678）的 035、037、038、039、040 版本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null. <para><paramref name="data"/> 为 null。</para></exception>
    /// <exception cref="DexException">The magic, version, endian tag, header size, file size, Adler-32 checksum or any table range is invalid. <para>魔数、版本、字节序标记、头部长度、文件大小、Adler-32 校验和或任一表范围非法。</para></exception>
    public static DexFile Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return DexReader.Parse(data);
    }

    /// <summary>
    /// Serializes the current model into DEX bytes (rebuilds all tables, the map list, and checksum/signature).
    /// <para>把当前模型序列化为 DEX 字节（重建所有表、map_list 与校验和/签名）。</para>
    /// </summary>
    /// <returns>
    /// Freshly allocated DEX bytes whose SHA-1 signature and Adler-32 checksum are recomputed over
    /// the rebuilt layout.
    /// <para>新分配的 DEX 字节；其 SHA-1 签名与 Adler-32 校验和按重建后的布局重新计算。</para>
    /// </returns>
    public byte[] ToArray() => DexWriter.Write(this);

    /// <summary>
    /// Rebuilds the current model to the specified path.
    /// <para>把当前模型重建写出到指定路径。</para>
    /// </summary>
    /// <param name="path">
    /// Destination file. An existing file is replaced only after the full image serializes, via an
    /// atomic temporary-file swap; on failure the previous content stays in place.
    /// <para>目标文件。仅当整份镜像序列化完成后才通过临时文件原子替换既有文件；失败时原内容保持不变。</para>
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or white space. <para><paramref name="path"/> 为 null 或空白。</para></exception>
    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = ToArray();
        Core.FileExtraction.WriteAtomic(path, stream => stream.Write(bytes));
    }

    /// <summary>
    /// Produces a description summary (version and table statistics).
    /// <para>生成文件描述摘要（版本与各表统计）。</para>
    /// </summary>
    /// <returns>
    /// A single-line version and per-table count summary, followed by up to the first 16 class names
    /// with their declared method counts (truncated with a "...N more" tail).
    /// <para>包含版本与各表数量摘要的单行文本，随后列出前 16 个类名及其声明方法数（超出部分以 "...N more" 结尾）。</para>
    /// </returns>
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
    /// <param name="index">Zero-based string_ids index; negative values are treated as out of range. <para>从零开始的 string_ids 索引；负值视为越界。</para></param>
    /// <returns>The decoded string, or <see cref="string.Empty"/> when the index is out of range.
    /// <para>解码后的字符串；索引越界时返回 <see cref="string.Empty"/>。</para></returns>
    public string GetString(int index) => (uint)index < Strings.Count ? Strings[index] : string.Empty;

    /// <summary>
    /// Gets a type descriptor by index.
    /// <para>按索引获取类型描述符。</para>
    /// </summary>
    /// <param name="index">Zero-based type_ids index; negative values are treated as out of range. <para>从零开始的 type_ids 索引；负值视为越界。</para></param>
    /// <returns>The descriptor, or <c>"?"</c> when the index is out of range.
    /// <para>类型描述符；索引越界时返回 <c>"?"</c>。</para></returns>
    public string GetType(int index) => (uint)index < Types.Count ? Types[index] : "?";

    /// <summary>
    /// Gets a field reference by index.
    /// <para>按索引获取字段引用。</para>
    /// </summary>
    /// <param name="index">Zero-based field_ids index; negative values are treated as out of range. <para>从零开始的 field_ids 索引；负值视为越界。</para></param>
    /// <returns>The field entry, or <see langword="null"/> when the index is out of range.
    /// <para>字段条目；索引越界时返回 <see langword="null"/>。</para></returns>
    public FieldId? GetField(int index) => (uint)index < Fields.Count ? Fields[index] : null;

    /// <summary>
    /// Gets a method reference by index.
    /// <para>按索引获取方法引用。</para>
    /// </summary>
    /// <param name="index">Zero-based method_ids index; negative values are treated as out of range. <para>从零开始的 method_ids 索引；负值视为越界。</para></param>
    /// <returns>The method entry, or <see langword="null"/> when the index is out of range.
    /// <para>方法条目；索引越界时返回 <see langword="null"/>。</para></returns>
    public MethodId? GetMethod(int index) => (uint)index < Methods.Count ? Methods[index] : null;

    /// <summary>
    /// Gets a prototype by index.
    /// <para>按索引获取原型。</para>
    /// </summary>
    /// <param name="index">Zero-based proto_ids index; negative values are treated as out of range. <para>从零开始的 proto_ids 索引；负值视为越界。</para></param>
    /// <returns>The prototype, or <see langword="null"/> when the index is out of range.
    /// <para>原型定义；索引越界时返回 <see langword="null"/>。</para></returns>
    public ProtoDefinition? GetProto(int index) => (uint)index < Protos.Count ? Protos[index] : null;

    /// <summary>
    /// Formats a type descriptor into a readable form (e.g. "Lcom/x/Foo;" → "com.x.Foo").
    /// <para>把类型描述符格式化为可读形式（如 "Lcom/x/Foo;" → "com.x.Foo"）。</para>
    /// </summary>
    /// <param name="index">Zero-based type_ids index. <para>从零开始的 type_ids 索引。</para></param>
    /// <returns>The formatted descriptor, or <c>"?"</c> when the index is out of range.
    /// <para>格式化后的描述符；索引越界时返回 <c>"?"</c>。</para></returns>
    public string FormatType(int index)
    {
        var descriptor = GetType(index);
        return FormatDescriptor(descriptor);
    }

    /// <summary>
    /// Formats a type descriptor string into a readable form.
    /// <para>把类型描述符字符串格式化为可读形式。</para>
    /// </summary>
    /// <param name="descriptor">
    /// JVM-style descriptor: leading <c>[</c> per array dimension, a class form
    /// <c>Lpkg/Class;</c>, or a single-char primitive shorty. Never dereferenced past its own length.
    /// <para>JVM 风格描述符：每维数组以一个前导 <c>[</c> 表示，类形式为 <c>Lpkg/Class;</c>，
    /// 基本类型为单字符 shorty。解析不会越出其自身长度。</para>
    /// </param>
    /// <returns>
    /// <c>"?"</c> for null/empty input; class descriptors with <c>/</c> replaced by <c>.</c> and the
    /// <c>L…;</c> wrapper stripped; shorties mapped to names such as <c>int</c> or <c>void</c>; each
    /// array dimension appends one trailing <c>[</c>; anything unrecognized is returned unchanged.
    /// <para>null/空输入返回 <c>"?"</c>；类描述符去掉 <c>L…;</c> 包裹并把 <c>/</c> 替换为 <c>.</c>；
    /// shorty 映射为 <c>int</c>、<c>void</c> 等名称；每一维数组追加一个尾部 <c>[</c>；无法识别的内容原样返回。</para>
    /// </returns>
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
        return arrayDepth == 0 ? name : name + string.Concat(Enumerable.Repeat("[]", arrayDepth));
    }

    /// <summary>
    /// Formats a field reference as "owner-&gt;name:type".
    /// <para>把字段引用格式化为 "所有者-&gt;名称:类型" 形式。</para>
    /// </summary>
    /// <param name="field">Field entry whose out-of-range indices resolve through the tolerant <c>Get*</c> lookups. <para>字段条目；其越界索引经由宽松的 <c>Get*</c> 查询解析。</para></param>
    /// <returns>The formatted reference, using <c>"?"</c> and empty placeholders for unresolved indices.
    /// <para>格式化后的引用；无法解析的索引以 <c>"?"</c> 与空占位呈现。</para></returns>
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
    /// <param name="method">Method entry; when its proto index is out of range the parameter list is empty and the return type shows <c>"?"</c>. <para>方法条目；原型索引越界时参数列表为空、返回类型显示 <c>"?"</c>。</para></param>
    /// <returns>The formatted reference with comma-separated formatted parameter types.
    /// <para>格式化后的引用，参数类型以逗号分隔并已格式化。</para></returns>
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