namespace UotanStudio.Firmware.Dex.Model;

/// <summary>
/// <para>类定义（class_def_item）：类索引、访问标志、父类、接口与类数据。</para>
/// Class definition (class_def_item): class index, access flags, superclass, interfaces and class data.
/// </summary>
public sealed class ClassDefinition
{
    /// <summary>类类型索引（type_ids）。<para>Class type index (type_ids).</para></summary>
    public int TypeIndex { get; set; }

    /// <summary>访问标志。<para>Access flags.</para></summary>
    public uint AccessFlags { get; set; }

    /// <summary>父类类型索引；无父类为 0xFFFFFFFF。<para>Superclass type index; 0xFFFFFFFF when none.</para></summary>
    public int SuperclassIndex { get; set; } = -1;

    /// <summary>接口类型索引数组（type_ids）。<para>Interface type index array (type_ids).</para></summary>
    public int[] InterfaceTypeIndices { get; set; } = [];

    /// <summary>源文件名索引（string_ids）；无源文件为 0xFFFFFFFF。<para>Source file name index; 0xFFFFFFFF when none.</para></summary>
    public int SourceFileIndex { get; set; } = -1;

    /// <summary>静态字段（class_data 顺序）。<para>Static fields (class_data order).</para></summary>
    public List<EncodedField> StaticFields { get; } = [];

    /// <summary>实例字段（class_data 顺序）。<para>Instance fields (class_data order).</para></summary>
    public List<EncodedField> InstanceFields { get; } = [];

    /// <summary>直接方法（class_data 顺序）。<para>Direct methods (class_data order).</para></summary>
    public List<EncodedMethod> DirectMethods { get; } = [];

    /// <summary>虚方法（class_data 顺序）。<para>Virtual methods (class_data order).</para></summary>
    public List<EncodedMethod> VirtualMethods { get; } = [];

    /// <summary>
    /// <para>获取全部字段（静态 + 实例）。</para>
    /// Gets all fields (static + instance).
    /// </summary>
    public IEnumerable<EncodedField> AllFields => StaticFields.Concat(InstanceFields);

    /// <summary>
    /// <para>获取全部方法（直接 + 虚）。</para>
    /// Gets all methods (direct + virtual).
    /// </summary>
    public IEnumerable<EncodedMethod> AllMethods => DirectMethods.Concat(VirtualMethods);

    /// <summary>
    /// <para>注解目录（annotations_directory_item）；无注解为 <c>null</c>（annotations_off = 0）。</para>
    /// Annotations directory (annotations_directory_item); <c>null</c> when there are none (annotations_off = 0).
    /// </summary>
    public AnnotationsDirectory? Annotations { get; set; }

    /// <summary>
    /// <para>静态字段初始值（encoded_array，长度与 StaticFields 一致）；无初始值为 <c>null</c>（static_values_off = 0）。</para>
    /// Static field initial values (encoded_array, same length as StaticFields); <c>null</c> when absent (static_values_off = 0).
    /// </summary>
    public List<EncodedValue>? StaticValues { get; set; }
}