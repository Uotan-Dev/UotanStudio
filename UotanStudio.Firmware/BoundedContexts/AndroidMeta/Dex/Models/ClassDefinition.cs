namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

/// <summary>
/// A DEX <c>class_def_item</c> with its interfaces, class data members, annotation directory and static-value array resolved into object model lists.
/// <para>DEX 的 <c>class_def_item</c>，其接口、class data 成员、注解目录与静态值数组均已解析为对象模型列表。</para>
/// </summary>
public sealed class ClassDefinition
{
    /// <summary>
    /// Index of this class descriptor in the <c>type_ids</c> table.
    /// <para>本类描述符在 <c>type_ids</c> 表中的索引。</para>
    /// </summary>
    /// <value>A zero-based index kept exactly as stored; the declaring file must contain that type entry.
    /// <para>按原值保存的从零开始索引，所在文件必须包含对应的类型条目。</para></value>
    public int TypeIndex { get; set; }

    /// <summary>
    /// Raw <c>access_flags</c> word for the class.
    /// <para>类原始的 <c>access_flags</c> 字。</para>
    /// </summary>
    /// <value>A bit mask of the DEX flags in <c>AccessFlags</c>; bits outside that mask are preserved unchanged.
    /// <para>为 <c>AccessFlags</c> 中 DEX 标志位的掩码组合；掩码之外的位原样保留。</para></value>
    public uint AccessFlags { get; set; }

    /// <summary>
    /// Index of the direct superclass in the <c>type_ids</c> table.
    /// <para>直接父类在 <c>type_ids</c> 表中的索引。</para>
    /// </summary>
    /// <value><c>-1</c> when the file stores the no-index sentinel, i.e. for <c>java.lang.Object</c> and synthetic roots; otherwise a zero-based type index.
    /// <para>文件保存 no-index 哨兵值时为 <c>-1</c>（即 <c>java.lang.Object</c> 与合成根类）；否则为从零开始的类型索引。</para></value>
    public int SuperclassIndex { get; set; } = -1;

    /// <summary>
    /// Type indices of the declared interfaces, in the order of the source <c>type_list</c>.
    /// <para>声明接口的类型索引，顺序与源 <c>type_list</c> 一致。</para>
    /// </summary>
    /// <value>Empty when the class defines no interface list.
    /// <para>类未定义接口列表时为空。</para></value>
    public int[] InterfaceTypeIndices { get; set; } = [];

    /// <summary>
    /// Index of the originating source file name in the <c>string_ids</c> table.
    /// <para>源文件名在 <c>string_ids</c> 表中的索引。</para>
    /// </summary>
    /// <value><c>-1</c> when the file stores the no-index sentinel, i.e. no source file is recorded.
    /// <para>文件保存 no-index 哨兵值时为 <c>-1</c>，即未记录源文件名。</para></value>
    public int SourceFileIndex { get; set; } = -1;

    /// <summary>
    /// Static fields from the class data section, in encoded order.
    /// <para>来自 class data 段的静态字段，保持编码顺序。</para>
    /// </summary>
    /// <value>Empty for a class without static fields; entries are decoded lazily against the owning file.
    /// <para>无静态字段时为空；条目相对所属文件按需解码。</para></value>
    public List<EncodedField> StaticFields { get; } = [];

    /// <summary>
    /// Instance fields from the class data section, in encoded order.
    /// <para>来自 class data 段的实例字段，保持编码顺序。</para>
    /// </summary>
    /// <value>Empty for a class without instance fields. <para>无实例字段时为空。</para></value>
    public List<EncodedField> InstanceFields { get; } = [];

    /// <summary>
    /// Direct methods (static, private and constructors) from the class data section.
    /// <para>class data 段中的直接方法（静态、私有与构造方法）。</para>
    /// </summary>
    /// <value>Empty for an interface or a class without direct methods.
    /// <para>接口或无直接方法的类为空。</para></value>
    public List<EncodedMethod> DirectMethods { get; } = [];

    /// <summary>
    /// Virtual methods from the class data section.
    /// <para>class data 段中的虚方法。</para>
    /// </summary>
    /// <value>Empty for a class that declares no overridable methods.
    /// <para>类未声明可重写方法时为空。</para></value>
    public List<EncodedMethod> VirtualMethods { get; } = [];

    /// <summary>
    /// Static fields followed by instance fields, without reordering either group.
    /// <para>先静态字段后实例字段，两组内部顺序不变。</para>
    /// </summary>
    /// <value>A lazily concatenated view over the two field lists, so edits to them show through immediately.
    /// <para>对两个字段列表的惰性拼接视图，因此对它们的修改会立即体现。</para></value>
    public IEnumerable<EncodedField> AllFields => StaticFields.Concat(InstanceFields);

    /// <summary>
    /// Direct methods followed by virtual methods, without reordering either group.
    /// <para>先直接方法后虚方法，两组内部顺序不变。</para>
    /// </summary>
    /// <value>A lazily concatenated view over the two method lists.
    /// <para>对两个方法列表的惰性拼接视图。</para></value>
    public IEnumerable<EncodedMethod> AllMethods => DirectMethods.Concat(VirtualMethods);

    /// <summary>
    /// The class's <c>annotations_directory_item</c>: class, field, method and parameter annotation sets.
    /// <para>该类的 <c>annotations_directory_item</c>：类、字段、方法与参数注解集合。</para>
    /// </summary>
    /// <value><see langword="null"/> when the class declares no annotation directory.
    /// <para>类未声明注解目录时为 <see langword="null"/>。</para></value>
    public AnnotationsDirectory? Annotations { get; set; }

    /// <summary>
    /// The <c>encoded_array_item</c> of initial values referenced by the class definition.
    /// <para>类定义引用的初始值 <c>encoded_array_item</c>。</para>
    /// </summary>
    /// <value><see langword="null"/> when no static values are stored; otherwise aligned positionally with <see cref="StaticFields"/>.
    /// <para>未保存静态初值时为 <see langword="null"/>；否则与 <see cref="StaticFields"/> 按位置一一对应。</para></value>
    public List<EncodedValue>? StaticValues { get; set; }
}