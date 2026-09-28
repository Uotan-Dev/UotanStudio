namespace UotanStudio.Firmware.AndroidMeta.Dex.Models;

/// <summary>
/// One name/value pair inside an encoded annotation.
/// <para>编码注解内的一个名称与取值对。</para>
/// </summary>
public sealed class AnnotationElement
{
    /// <summary>Index into the string identifiers table naming this element.
    /// <para>指向字符串标识符表、给出本元素名称的索引。</para></summary>
    /// <value>Zero-based <c>string_id</c> offset; the reader does not bounds-check it, so an out-of-range
    /// value from a damaged file stays as read.
    /// <para>从零开始的 <c>string_id</c> 序号；读取方不做边界检查，损坏文件中越界的值会原样保留。</para></value>
    public int NameIndex { get; set; }

    /// <summary>Value stored for this element.
    /// <para>本元素存储的取值。</para></summary>
    /// <value>A decoded <see cref="EncodedValue"/>; <see cref="EncodedValue.Null"/> typed with a
    /// <see langword="null"/> payload until a reader or writer assigns a real value.
    /// <para>解码后的 <see cref="EncodedValue"/>；尚未赋入真实值时，类型为
    /// <see cref="EncodedValue.Null"/> 且载荷为 <see langword="null"/>。</para></value>
    public EncodedValue Value { get; set; } = EncodedValue.Null();
}

/// <summary>
/// Visibility bytes recorded in an <c>annotation_item</c>.
/// <para><c>annotation_item</c> 中记录的可见性字节。</para>
/// </summary>
internal static class AnnotationVisibility
{
    /// <summary>Visibility <c>0x00</c>: kept for build tooling only. <para>可见性 <c>0x00</c>：仅供构建工具保留。</para></summary>
    public const int Build = 0x00;

    /// <summary>Visibility <c>0x01</c>: carried into the runtime. <para>可见性 <c>0x01</c>：带入运行时。</para></summary>
    public const int Runtime = 0x01;

    /// <summary>Visibility <c>0x02</c>: visible to system tooling. <para>可见性 <c>0x02</c>：对系统工具可见。</para></summary>
    public const int System = 0x02;
}

/// <summary>
/// A decoded annotation instance: its type, its elements and the visibility byte.
/// <para>解码后的注解实例：包含其类型、元素以及可见性字节。</para>
/// </summary>
public sealed class AnnotationItem
{
    /// <summary>Index into the type identifiers table of the annotated annotation class.
    /// <para>指向类型标识符表、给出被注解的注解类的索引。</para></summary>
    /// <value>Zero-based <c>type_id</c> offset; unvalidated against the table size when read from
    /// untrusted input.
    /// <para>从零开始的 <c>type_id</c> 序号；来自不可信输入时不会校验其是否超出表长。</para></value>
    public int TypeIndex { get; set; }

    /// <summary>Element/value pairs in the order stored by the <c>encoded_annotation</c>.
    /// <para>按 <c>encoded_annotation</c> 存储顺序排列的元素与取值对。</para></summary>
    /// <value>A list that is always non-null and stays empty for an annotation without elements;
    /// its length equals the element count that was read.
    /// <para>该列表始终不为 <see langword="null"/>，无元素的注解保持为空；其长度等于实际读取到的元素数量。</para></value>
    public List<AnnotationElement> Elements { get; } = [];

    /// <summary>Raw visibility byte of the containing <c>annotation_item</c>.
    /// <para>所属 <c>annotation_item</c> 的原始可见性字节。</para></summary>
    /// <value>One of the <see cref="AnnotationVisibility"/> constants; values outside that set are kept
    /// as read, and <see cref="AnnotationVisibility.Build"/> is the default.
    /// <para>取值为 <see cref="AnnotationVisibility"/> 常量之一；集合之外的值按原样保留，默认值为
    /// <see cref="AnnotationVisibility.Build"/>。</para></value>
    public int Visibility { get; set; } = AnnotationVisibility.Build;
}

/// <summary>
/// A set of annotations attached to one target, mirroring <c>annotation_set_item</c>.
/// <para>附加到单个目标上的一组注解，对应 <c>annotation_set_item</c>。</para>
/// </summary>
public sealed class AnnotationSet
{
    /// <summary>Annotation items in stored order.
    /// <para>按存储顺序排列的注解项。</para></summary>
    /// <value>Always non-null; empty when the set carries no annotation, which the reader represents by
    /// a zero-size set rather than <see langword="null"/>.
    /// <para>始终不为 <see langword="null"/>；集合不含注解时为空，读取方以数量为 <c>0</c> 的集合表示，而不是 <see langword="null"/>。</para></value>
    public List<AnnotationItem> Annotations { get; } = [];

    /// <summary>Whether the set carries no annotation.
    /// <para>该集合是否不含任何注解。</para></summary>
    /// <value><see langword="true"/> when <see cref="Annotations"/> has zero items; otherwise
    /// <see langword="false"/>.
    /// <para><see cref="Annotations"/> 项数为 <c>0</c> 时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    public bool IsEmpty => Annotations.Count == 0;
}

/// <summary>
/// Per-parameter list of annotation sets, mirroring <c>annotation_set_ref_list</c>.
/// <para>逐参数的注解集列表，对应 <c>annotation_set_ref_list</c>。</para>
/// </summary>
public sealed class AnnotationSetRefList
{
    /// <summary>One entry per parameter, in parameter order.
    /// <para>每个参数一项，按参数顺序排列。</para></summary>
    /// <value>Always non-null; an entry is <see langword="null"/> when that parameter's offset was
    /// <c>0</c>, meaning the parameter carries no annotations.
    /// <para>始终不为 <see langword="null"/>；当某参数的偏移为 <c>0</c> 时对应项为 <see langword="null"/>，表示该参数没有注解。</para></value>
    public List<AnnotationSet?> Sets { get; } = [];
}

/// <summary>
/// Annotations attached to a single field, one row of the field annotations table.
/// <para>附加到单个字段上的注解，即字段注解表中的一行。</para>
/// </summary>
public sealed class FieldAnnotation
{
    /// <summary>Index into the field identifiers table.
    /// <para>指向字段标识符表的索引。</para></summary>
    /// <value>Zero-based <c>field_id</c> offset as stored by the directory.
    /// <para>目录中记录的、从零开始的 <c>field_id</c> 序号。</para></value>
    public int FieldIndex { get; set; }

    /// <summary>Annotations held by this field.
    /// <para>该字段持有的注解。</para></summary>
    /// <value>Never <see langword="null"/>; a fresh empty set means the field has no annotations.
    /// <para>从不为 <see langword="null"/>；全新空集合表示该字段没有注解。</para></value>
    public AnnotationSet Annotations { get; set; } = new();
}

/// <summary>
/// Annotations attached to a single method, one row of the method annotations table.
/// <para>附加到单个方法上的注解，即方法注解表中的一行。</para>
/// </summary>
public sealed class MethodAnnotation
{
    /// <summary>Index into the method identifiers table.
    /// <para>指向方法标识符表的索引。</para></summary>
    /// <value>Zero-based <c>method_id</c> offset as stored by the directory.
    /// <para>目录中记录的、从零开始的 <c>method_id</c> 序号。</para></value>
    public int MethodIndex { get; set; }

    /// <summary>Annotations held by this method.
    /// <para>该方法持有的注解。</para></summary>
    /// <value>Never <see langword="null"/>; a fresh empty set means the method has no annotations.
    /// <para>从不为 <see langword="null"/>；全新空集合表示该方法没有注解。</para></value>
    public AnnotationSet Annotations { get; set; } = new();
}

/// <summary>
/// Parameter annotations of a single method, one row of the parameter annotations table.
/// <para>单个方法的参数注解，即参数注解表中的一行。</para>
/// </summary>
public sealed class ParameterAnnotation
{
    /// <summary>Index into the method identifiers table.
    /// <para>指向方法标识符表的索引。</para></summary>
    /// <value>Zero-based <c>method_id</c> offset as stored by the directory.
    /// <para>目录中记录的、从零开始的 <c>method_id</c> 序号。</para></value>
    public int MethodIndex { get; set; }

    /// <summary>Annotation sets keyed by parameter position.
    /// <para>按参数位置组织的注解集。</para></summary>
    /// <value>Never <see langword="null"/>; its <see cref="AnnotationSetRefList.Sets"/> entries map one
    /// position to either a set or <see langword="null"/>.
    /// <para>从不为 <see langword="null"/>；其 <see cref="AnnotationSetRefList.Sets"/> 项把每个位置映射为一个集合或 <see langword="null"/>。</para></value>
    public AnnotationSetRefList Parameters { get; set; } = new();
}

/// <summary>
/// The whole <c>annotations_directory_item</c> of one class def: class, field, method and parameter
/// annotations.
/// <para>单个 class def 的完整 <c>annotations_directory_item</c>：包含类、字段、方法与参数注解。</para>
/// </summary>
public sealed class AnnotationsDirectory
{
    /// <summary>Annotation set applied to the class itself.
    /// <para>应用于类本身的注解集。</para></summary>
    /// <value><see langword="null"/> when the directory has no class annotation offset; a non-null set can
    /// still be empty.
    /// <para>目录中没有类注解偏移时为 <see langword="null"/>；非 <see langword="null"/> 的集合仍可能为空。</para></value>
    public AnnotationSet? ClassAnnotations { get; set; }

    /// <summary>Field annotation rows in stored order.
    /// <para>按存储顺序排列的字段注解行。</para></summary>
    /// <value>Always non-null; empty when no field of this class is annotated.
    /// <para>始终不为 <see langword="null"/>；该类没有字段被注解时为空。</para></value>
    public List<FieldAnnotation> FieldAnnotations { get; } = [];

    /// <summary>Method annotation rows in stored order.
    /// <para>按存储顺序排列的方法注解行。</para></summary>
    /// <value>Always non-null; empty when no method of this class is annotated.
    /// <para>始终不为 <see langword="null"/>；该类没有方法被注解时为空。</para></value>
    public List<MethodAnnotation> MethodAnnotations { get; } = [];

    /// <summary>Parameter annotation rows in stored order.
    /// <para>按存储顺序排列的参数注解行。</para></summary>
    /// <value>Always non-null; empty when no method of this class has parameter annotations.
    /// <para>始终不为 <see langword="null"/>；该类没有方法带参数注解时为空。</para></value>
    public List<ParameterAnnotation> ParameterAnnotations { get; } = [];

    /// <summary>Whether the directory records no annotation content at all.
    /// <para>该目录是否完全没有记录任何注解内容。</para></summary>
    /// <value><see langword="true"/> when <see cref="ClassAnnotations"/> is <see langword="null"/> or
    /// empty and all three row lists are empty; otherwise <see langword="false"/>.
    /// <para><see cref="ClassAnnotations"/> 为 <see langword="null"/> 或空，且三个行列表均为空时为
    /// <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    public bool IsEmpty =>
        (ClassAnnotations == null || ClassAnnotations.IsEmpty)
        && FieldAnnotations.Count == 0
        && MethodAnnotations.Count == 0
        && ParameterAnnotations.Count == 0;
}