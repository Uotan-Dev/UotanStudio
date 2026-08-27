namespace UotanStudio.Firmware.Dex.Model;

/// <summary>
/// <para>注解元素（annotation_element）：名称索引 + 编码值。</para>
/// Annotation element (annotation_element): name index + encoded value.
/// </summary>
public sealed class AnnotationElement
{
    /// <summary>元素名称字符串索引（string_ids）。<para>Element name string index (string_ids).</para></summary>
    public int NameIndex { get; set; }

    /// <summary>元素值。<para>Element value.</para></summary>
    public EncodedValue Value { get; set; } = EncodedValue.Null();
}

/// <summary>
/// <para>注解可见性（annotation_set_item 条目高字节）。</para>
/// Annotation visibility (high byte of an annotation_set_item entry).
/// </summary>
public static class AnnotationVisibility
{
    /// <summary>构建期可见。<para>Build-time only.</para></summary>
    public const int Build = 0x00;

    /// <summary>运行时可见。<para>Runtime-visible.</para></summary>
    public const int Runtime = 0x01;

    /// <summary>系统可见。<para>System-visible.</para></summary>
    public const int System = 0x02;
}

/// <summary>
/// <para>注解项（encoded_annotation / annotation_item）：注解类型 + 元素列表。</para>
/// Annotation item (encoded_annotation / annotation_item): annotation type + element list.
/// </summary>
public sealed class AnnotationItem
{
    /// <summary>注解类型索引（type_ids）。<para>Annotation type index (type_ids).</para></summary>
    public int TypeIndex { get; set; }

    /// <summary>注解元素。<para>Annotation elements.</para></summary>
    public List<AnnotationElement> Elements { get; } = [];

    /// <summary>可见性（AnnotationVisibility）。<para>Visibility (AnnotationVisibility).</para></summary>
    public int Visibility { get; set; } = AnnotationVisibility.Build;
}

/// <summary>
/// <para>注解集合（annotation_set_item）：指向一组注解项。</para>
/// Annotation set (annotation_set_item): a set of annotation items.
/// </summary>
public sealed class AnnotationSet
{
    /// <summary>注解项（文件顺序）。<para>Annotation items (file order).</para></summary>
    public List<AnnotationItem> Annotations { get; } = [];

    /// <summary>注解项是否为空。<para>Whether the set is empty.</para></summary>
    public bool IsEmpty => Annotations.Count == 0;
}

/// <summary>
/// <para>参数注解引用列表（annotation_set_ref_list）：每个参数一个注解集合（可为空）。</para>
/// Parameter annotation reference list (annotation_set_ref_list): one annotation set per parameter (may be null).
/// </summary>
public sealed class AnnotationSetRefList
{
    /// <summary>
    /// <para>每个参数对应的注解集合；<c>null</c> 表示该参数无注解（offset 0）。</para>
    /// Annotation set per parameter; <c>null</c> means no annotations for that parameter (offset 0).
    /// </summary>
    public List<AnnotationSet?> Sets { get; } = [];
}

/// <summary>
/// <para>字段注解条目（field_annotation）：字段索引 + 注解集合。</para>
/// Field annotation entry (field_annotation): field index + annotation set.
/// </summary>
public sealed class FieldAnnotation
{
    /// <summary>字段索引（field_ids）。<para>Field index (field_ids).</para></summary>
    public int FieldIndex { get; set; }

    /// <summary>注解集合。<para>Annotation set.</para></summary>
    public AnnotationSet Annotations { get; set; } = new();
}

/// <summary>
/// <para>方法注解条目（method_annotation）：方法索引 + 注解集合。</para>
/// Method annotation entry (method_annotation): method index + annotation set.
/// </summary>
public sealed class MethodAnnotation
{
    /// <summary>方法索引（method_ids）。<para>Method index (method_ids).</para></summary>
    public int MethodIndex { get; set; }

    /// <summary>注解集合。<para>Annotation set.</para></summary>
    public AnnotationSet Annotations { get; set; } = new();
}

/// <summary>
/// <para>参数注解条目（parameter_annotation）：方法索引 + 参数注解引用列表。</para>
/// Parameter annotation entry (parameter_annotation): method index + parameter annotation ref list.
/// </summary>
public sealed class ParameterAnnotation
{
    /// <summary>方法索引（method_ids）。<para>Method index (method_ids).</para></summary>
    public int MethodIndex { get; set; }

    /// <summary>参数注解引用列表。<para>Parameter annotation ref list.</para></summary>
    public AnnotationSetRefList Parameters { get; set; } = new();
}

/// <summary>
/// <para>注解目录（annotations_directory_item）：类级注解 + 字段/方法/参数注解条目。</para>
/// Annotations directory (annotations_directory_item): class annotations + field/method/parameter entries.
/// </summary>
public sealed class AnnotationsDirectory
{
    /// <summary>类级注解集合；无则为 <c>null</c>（offset 0）。<para>Class-level annotation set; <c>null</c> when absent (offset 0).</para></summary>
    public AnnotationSet? ClassAnnotations { get; set; }

    /// <summary>字段注解条目。<para>Field annotation entries.</para></summary>
    public List<FieldAnnotation> FieldAnnotations { get; } = [];

    /// <summary>方法注解条目。<para>Method annotation entries.</para></summary>
    public List<MethodAnnotation> MethodAnnotations { get; } = [];

    /// <summary>参数注解条目。<para>Parameter annotation entries.</para></summary>
    public List<ParameterAnnotation> ParameterAnnotations { get; } = [];

    /// <summary>是否为空（无可写内容，对应 annotations_off = 0）。<para>Whether the directory is empty (annotations_off = 0).</para></summary>
    public bool IsEmpty =>
        (ClassAnnotations == null || ClassAnnotations.IsEmpty)
        && FieldAnnotations.Count == 0
        && MethodAnnotations.Count == 0
        && ParameterAnnotations.Count == 0;
}