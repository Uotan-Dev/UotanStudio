namespace UotanStudio.Services.Projects.Schemes;

/// <summary>
/// Value type of a declared scheme input.
/// <para>声明式 Scheme 输入的取值类型。</para>
/// </summary>
public enum SchemeInputType
{
    /// <summary>Free-form text.<para>自由文本。</para></summary>
    Text,

    /// <summary>File path.<para>文件路径。</para></summary>
    Path,

    /// <summary>Directory path.<para>目录路径。</para></summary>
    Directory,

    /// <summary>Boolean flag.<para>布尔开关。</para></summary>
    Boolean,

    /// <summary>Number.<para>数值。</para></summary>
    Number,

    /// <summary>One value of <see cref="SchemeInputDefinition.Choices"/>.<para><see cref="SchemeInputDefinition.Choices"/> 中的一个取值。</para></summary>
    Choice
}

/// <summary>
/// Declared scheme input supplied by the caller before a run.
/// <para>运行前由调用方提供的声明式 Scheme 输入。</para>
/// </summary>
public sealed class SchemeInputDefinition
{
    /// <summary>
    /// Stable input identifier referenced by stage bindings.
    /// <para>阶段绑定引用的稳定输入标识符。</para>
    /// </summary>
    /// <value>Identifier unique inside the owning scheme.<para>所属 Scheme 内唯一的标识符。</para></value>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the input.
    /// <para>输入的显示名称。</para>
    /// </summary>
    /// <value>Human-readable caption shown by hosts.<para>宿主展示的可读标题。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Declared value type.
    /// <para>声明的取值类型。</para>
    /// </summary>
    /// <value>One of the <see cref="SchemeInputType"/> values; defaults to <see cref="SchemeInputType.Text"/>.
    /// <para><see cref="SchemeInputType"/> 中的一个值；默认是 <see cref="SchemeInputType.Text"/>。</para></value>
    public SchemeInputType Type { get; set; } = SchemeInputType.Text;

    /// <summary>
    /// Whether a run rejects a missing or empty value.
    /// <para>运行是否拒绝缺失或空的取值。</para>
    /// </summary>
    /// <value><see langword="true"/> when the caller must supply a value.<para>调用方必须提供取值时为 <see langword="true"/>。</para></value>
    public bool Required { get; set; }

    /// <summary>
    /// Optional default value used when the caller supplies none.
    /// <para>调用方未提供时使用的可选默认值。</para>
    /// </summary>
    /// <value>The default text, or <see langword="null"/> when the input has no default.
    /// <para>默认文本；输入没有默认值时为 <see langword="null"/>。</para></value>
    public string? Default { get; set; }

    /// <summary>
    /// Allowed values when <see cref="Type"/> is <see cref="SchemeInputType.Choice"/>.
    /// <para><see cref="Type"/> 为 <see cref="SchemeInputType.Choice"/> 时允许的取值。</para>
    /// </summary>
    /// <value>One entry per allowed value; empty for every other input type.<para>每个允许值一条记录；其他输入类型为空列表。</para></value>
    public List<string> Choices { get; set; } = [];

    /// <summary>
    /// Whether the value must be masked in UI and logs.
    /// <para>是否必须在界面与日志中遮蔽该取值。</para>
    /// </summary>
    /// <value><see langword="true"/> when the value is sensitive.<para>取值为敏感信息时为 <see langword="true"/>。</para></value>
    public bool Sensitive { get; set; }
}