namespace UotanStudio.Projects.Interactions;

/// <summary>
/// Serializable UI vocabulary; no host controls or executable scripts cross this boundary.
/// Field kinds occupy the contiguous range from <c>TextField</c> through <c>SavePathField</c>, and the host classifies kinds on that ordering.
/// <para>可序列化的 UI 词表；宿主控件与可执行脚本不跨越此边界。字段类型占据从 <c>TextField</c> 到 <c>SavePathField</c> 的连续区间，宿主依赖该顺序进行分类。</para>
/// </summary>
public enum InteractionElementKind
{
    /// <summary>Read-only text shown from the label; produces no value. <para>按标签展示的只读文本；不产生值。</para></summary>
    Text,
    /// <summary>Horizontal separator; produces no value. <para>不产生值的水平分隔线。</para></summary>
    Divider,
    /// <summary>Inline image carried in <c>ImageData</c>. <para>内嵌图片，字节由 <c>ImageData</c> 承载。</para></summary>
    Image,
    /// <summary>Free text capped at <c>MaxLength</c>. <para>自由文本；长度不超过 <c>MaxLength</c>。</para></summary>
    TextField,
    /// <summary>
    /// Decimal input constrained inclusively by <c>Minimum</c> and <c>Maximum</c>.
    /// <para>由 <c>Minimum</c> 与 <c>Maximum</c> 闭合界定的十进制输入。</para>
    /// </summary>
    NumberField,
    /// <summary>Checkbox stored in the <c>Boolean</c> slot. <para>状态存于 <c>Boolean</c> 槽的复选框。</para></summary>
    BooleanField,
    /// <summary>Single choice from <c>Options</c>, stored as text. <para>从 <c>Options</c> 单选，以文本存储。</para></summary>
    ChoiceField,
    /// <summary>
    /// Duplicate-free subset of <c>Options</c> stored in <c>Selection</c>.
    /// <para><c>Options</c> 中不重复条目的选择，存于 <c>Selection</c>。</para>
    /// </summary>
    MultiChoiceField,
    /// <summary>Text path that must name an existing file. <para>必须指向已存在文件的文本路径。</para></summary>
    FileField,
    /// <summary>Text path that must name an existing directory. <para>必须指向已存在目录的文本路径。</para></summary>
    DirectoryField,
    /// <summary>
    /// Destination path whose characters are valid and whose file name is non-empty; the target itself need not exist.
    /// <para>路径字符合法且文件名非空的目标路径；目标本身不必已存在。</para>
    /// </summary>
    SavePathField,
    /// <summary>Horizontal layout container. <para>水平布局容器。</para></summary>
    Row,
    /// <summary>Vertical layout container. <para>垂直布局容器。</para></summary>
    Column,
    /// <summary>Framed layout container. <para>带边框的布局容器。</para></summary>
    Group,
    /// <summary>Tabbed container placing each child on its own page. <para>选项式容器，每个子元素独占一页。</para></summary>
    Pages,
}

/// <summary>
/// A scalar or list value whose representation is independent of the UI host.
/// <para>标量或列表值，其表示形式独立于 UI 宿主。</para>
/// </summary>
public sealed record InteractionValue
{
    /// <summary>
    /// Text slot used by text, choice and path fields; an unset slot is <see langword="null"/>.
    /// <para>文本、单选与路径字段使用的文本槽；未设置时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>
    /// The stored text, or <see langword="null"/> when this slot carries no value.
    /// <para>存储的文本；该槽无值时为 <see langword="null"/>。</para>
    /// </value>
    public string? Text { get; init; }

    /// <summary>
    /// Number slot used by <c>NumberField</c> elements.
    /// <para>由 <c>NumberField</c> 元素使用的数值槽。</para>
    /// </summary>
    /// <value>
    /// The stored decimal, or <see langword="null"/> when no number has been entered.
    /// <para>存储的十进制数；尚未输入数字时为 <see langword="null"/>。</para>
    /// </value>
    public decimal? Number { get; init; }

    /// <summary>
    /// Boolean slot used by <c>BooleanField</c> elements.
    /// <para>由 <c>BooleanField</c> 元素使用的布尔槽。</para>
    /// </summary>
    /// <value>
    /// The stored flag, or <see langword="null"/> when the checkbox has no state yet.
    /// <para>存储的标志；复选框尚无状态时为 <see langword="null"/>。</para>
    /// </value>
    public bool? Boolean { get; init; }

    /// <summary>
    /// List slot used by <c>MultiChoiceField</c> elements; entries repeat no <c>Options</c> value.
    /// <para>由 <c>MultiChoiceField</c> 元素使用的列表槽；条目不重复任何 <c>Options</c> 值。</para>
    /// </summary>
    /// <value>
    /// The selected entries, or <see langword="null"/> when the slot is unset; an empty array means nothing is selected.
    /// <para>已选条目；槽未设置时为 <see langword="null"/>；空数组表示未选中任何项。</para>
    /// </value>
    public string[]? Selection { get; init; }

    /// <summary>
    /// Renders a fixed placeholder so stringifying a value never exposes form data.
    /// <para>渲染固定占位符，将值转为字符串时不会暴露表单数据。</para>
    /// </summary>
    /// <returns>
    /// The literal <c>[interaction value]</c>, independent of the stored slots.
    /// <para>固定字面量 <c>[interaction value]</c>，与存储的槽位无关。</para>
    /// </returns>
    public override string ToString() => "[interaction value]";
}

/// <summary>
/// A field dependency evaluated without running a workflow or arbitrary expression.
/// <para>字段依赖关系，求值时不运行工作流或任意表达式。</para>
/// </summary>
public sealed record InteractionCondition
{
    /// <summary>
    /// Identifier of the field to read; the host rejects a form whose condition points at a missing or non-field element.
    /// <para>要读取的字段标识符；若条件指向缺失元素或非字段元素，宿主会拒绝该表单。</para>
    /// </summary>
    /// <value>
    /// The referenced field id, resolved with ordinal comparison.
    /// <para>被引用的字段 id，按序数比较解析。</para>
    /// </value>
    public required string FieldId { get; init; }

    /// <summary>
    /// Value the referenced field must equal; the condition holds only when the <c>Text</c>, <c>Number</c>, <c>Boolean</c> and <c>Selection</c> slots all match.
    /// <para>被引用字段必须相等的值；仅当 <c>Text</c>、<c>Number</c>、<c>Boolean</c> 与 <c>Selection</c> 槽全部匹配时条件才成立。</para>
    /// </summary>
    /// <value>
    /// The expected value; an unset <c>Selection</c> is compared as an empty selection.
    /// <para>期望值；未设置的 <c>Selection</c> 按空选择参与比较。</para>
    /// </value>
    public required InteractionValue EqualsValue { get; init; }
}

/// <summary>
/// A stable field, content element, or layout container.
/// <para>稳定的字段、内容元素或布局容器。</para>
/// </summary>
public sealed record InteractionElement
{
    /// <summary>
    /// Identifier kept stable while labels evolve; it keys submitted values and derived workflow ports.
    /// <para>在标签变动时保持稳定的标识符；用作已提交值与派生工作流端点的键。</para>
    /// </summary>
    /// <value>
    /// The id; the host requires it non-empty, at most 128 characters, and unique across the form.
    /// <para>标识符；宿主要求其非空、至多 128 个字符，并在整个表单内唯一。</para>
    /// </value>
    public required string Id { get; init; }

    /// <summary>
    /// Declared role of the element among content, field and container kinds.
    /// <para>元素在内容、字段与容器类型中的声明角色。</para>
    /// </summary>
    /// <value>
    /// The kind; the host rejects undefined values and treats the field range as editable input.
    /// <para>类型；宿主拒绝未定义值，并将字段区间视为可编辑输入。</para>
    /// </value>
    public InteractionElementKind Kind { get; init; }

    /// <summary>
    /// Visible caption; for <c>Text</c> elements it is the displayed content.
    /// <para>可见标题；对 <c>Text</c> 元素即为展示内容。</para>
    /// </summary>
    /// <value>
    /// The caption, at most 16,384 characters; empty when the element shows no caption.
    /// <para>标题文本，至多 16,384 个字符；元素无标题时为空字符串。</para>
    /// </value>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Helper text shown near the element.
    /// <para>在元素附近显示的辅助文本。</para>
    /// </summary>
    /// <value>
    /// The description, at most 65,536 characters; empty when absent.
    /// <para>描述文本，至多 65,536 个字符；无描述时为空字符串。</para>
    /// </value>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Encoded image bytes; <c>Image</c> elements require a non-empty value, each element is capped at 8 MiB and the whole form at 16 MiB.
    /// <para>编码后的图片字节；<c>Image</c> 元素要求非空值，单个元素至多 8 MiB，整个表单至多 16 MiB。</para>
    /// </summary>
    /// <value>
    /// The raw bytes, or <see langword="null"/> when the element carries no image.
    /// <para>原始字节；元素不携带图片时为 <see langword="null"/>。</para>
    /// </value>
    public byte[]? ImageData { get; init; }

    /// <summary>
    /// Initial value seeded into the field when the form opens.
    /// <para>表单打开时写入字段的初始值。</para>
    /// </summary>
    /// <value>
    /// The seed, or <see langword="null"/> to use the kind default: boolean false, empty selection, unset number, empty text.
    /// <para>种子值；为 <see langword="null"/> 时按类型取默认值：布尔为 false、选择为空列表、数值未设置、文本为空。</para>
    /// </value>
    public InteractionValue? DefaultValue { get; init; }

    /// <summary>
    /// Marks the field as required regardless of any condition.
    /// <para>无条件标记字段为必填。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when an empty value fails validation; booleans additionally require a checked state.
    /// <para>空值会导致校验失败时为 <see langword="true"/>；布尔字段还要求处于勾选状态。</para>
    /// </value>
    public bool Required { get; init; }

    /// <summary>
    /// Flags the collected value as sensitive so the host masks it in the UI and on derived output ports.
    /// <para>将采集值标记为敏感，宿主会在界面及派生输出端点上遮蔽它。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the field holds sensitive data.
    /// <para>字段持有敏感数据时为 <see langword="true"/>。</para>
    /// </value>
    public bool Sensitive { get; init; }

    /// <summary>
    /// Upper bound, in characters, for values of text-like fields.
    /// <para>文本类字段值的字符数上限。</para>
    /// </summary>
    /// <value>
    /// The limit; the host accepts 1 to 1,048,576 and the default is 4096.
    /// <para>上限；宿主接受 1 到 1,048,576，默认值为 4096。</para>
    /// </value>
    public int MaxLength { get; init; } = 4096;

    /// <summary>
    /// Inclusive lower bound for <c>NumberField</c> values.
    /// <para><c>NumberField</c> 值的闭区间下界。</para>
    /// </summary>
    /// <value>
    /// The bound, or <see langword="null"/> when the field has no minimum.
    /// <para>边界值；字段无下界时为 <see langword="null"/>。</para>
    /// </value>
    public decimal? Minimum { get; init; }

    /// <summary>
    /// Inclusive upper bound for <c>NumberField</c> values.
    /// <para><c>NumberField</c> 值的闭区间上界。</para>
    /// </summary>
    /// <value>
    /// The bound, or <see langword="null"/> when the field has no maximum; the host rejects a maximum below <c>Minimum</c>.
    /// <para>边界值；字段无上界时为 <see langword="null"/>；上界小于 <c>Minimum</c> 时宿主会拒绝。</para>
    /// </value>
    public decimal? Maximum { get; init; }

    /// <summary>
    /// Choice entries for <c>ChoiceField</c> and <c>MultiChoiceField</c>: at most 1,000 entries, each at most 4,096 characters, distinct under ordinal comparison.
    /// <para><c>ChoiceField</c> 与 <c>MultiChoiceField</c> 的可选项：至多 1,000 条，每条至多 4,096 个字符，且按序数比较互不重复。</para>
    /// </summary>
    /// <value>
    /// The option array; empty when the element is not a choice field.
    /// <para>选项数组；元素不是选择类字段时为空。</para>
    /// </value>
    public string[] Options { get; init; } = [];

    /// <summary>
    /// Condition that hides the element while it does not hold; a hidden container hides its descendants too.
    /// <para>条件不成立时隐藏该元素；隐藏的容器同样会隐藏其子孙元素。</para>
    /// </summary>
    /// <value>
    /// The condition, or <see langword="null"/> when the element is always visible.
    /// <para>条件；元素始终可见时为 <see langword="null"/>。</para>
    /// </value>
    public InteractionCondition? VisibleWhen { get; init; }

    /// <summary>
    /// Condition that disables the element and, transitively, enabled-gated descendants; disabled fields skip validation and stay out of submitted values.
    /// <para>条件不成立时禁用该元素，并沿传递链禁用其子孙；禁用字段跳过校验且不出现在已提交值中。</para>
    /// </summary>
    /// <value>
    /// The condition, or <see langword="null"/> when visibility alone gates the element.
    /// <para>条件；仅由可见性决定元素状态时为 <see langword="null"/>。</para>
    /// </value>
    public InteractionCondition? EnabledWhen { get; init; }

    /// <summary>
    /// Condition that additionally requires the field while it holds, on top of <c>Required</c>.
    /// <para>条件成立时在 <c>Required</c> 之外额外要求填写该字段。</para>
    /// </summary>
    /// <value>
    /// The condition, or <see langword="null"/> when no conditional requirement applies.
    /// <para>条件；不存在条件性要求时为 <see langword="null"/>。</para>
    /// </value>
    public InteractionCondition? RequiredWhen { get; init; }

    /// <summary>
    /// Ordered child elements; only <c>Row</c>, <c>Column</c>, <c>Group</c> and <c>Pages</c> may hold children.
    /// <para>有序子元素；仅 <c>Row</c>、<c>Column</c>、<c>Group</c> 与 <c>Pages</c> 可以持有子元素。</para>
    /// </summary>
    /// <value>
    /// The child array, empty for non-container kinds; the host caps the tree at 16 levels and 256 elements per form.
    /// <para>子元素数组，非容器类型时为空；宿主限制树深至多 16 层、每个表单至多 256 个元素。</para>
    /// </value>
    public InteractionElement[] Children { get; init; } = [];
}

/// <summary>
/// A reusable form schema. Field IDs remain stable when labels change.
/// <para>可复用的表单架构。字段 id 在标签变化时保持稳定。</para>
/// </summary>
public sealed record InteractionForm
{
    /// <summary>
    /// Headline presented with the form.
    /// <para>随表单一起展示的标题。</para>
    /// </summary>
    /// <value>
    /// The title, at most 16,384 characters; empty when untitled.
    /// <para>标题文本，至多 16,384 个字符；无标题时为空字符串。</para>
    /// </value>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Root of the element tree; every other element is reached through its <c>Children</c>.
    /// <para>元素树的根节点；其余元素均通过其 <c>Children</c> 到达。</para>
    /// </summary>
    /// <value>
    /// The root element validated when the host builds the form state.
    /// <para>宿主构建表单状态时加以校验的根元素。</para>
    /// </value>
    public required InteractionElement Content { get; init; }

    /// <summary>
    /// Caption of the submit button.
    /// <para>提交按钮的文本。</para>
    /// </summary>
    /// <value>
    /// The caption, at most 1,024 characters, or <see langword="null"/> for the host default.
    /// <para>按钮文本，至多 1,024 个字符；为 <see langword="null"/> 时使用宿主默认值。</para>
    /// </value>
    public string? SubmitLabel { get; init; }

    /// <summary>
    /// Caption of the cancel button.
    /// <para>取消按钮的文本。</para>
    /// </summary>
    /// <value>
    /// The caption, at most 1,024 characters, or <see langword="null"/> for the host default.
    /// <para>按钮文本，至多 1,024 个字符；为 <see langword="null"/> 时使用宿主默认值。</para>
    /// </value>
    public string? CancelLabel { get; init; }
}

/// <summary>
/// An individual request owned by one workflow invocation.
/// <para>由单个工作流调用持有的独立请求。</para>
/// </summary>
public sealed record InteractionRequest
{
    /// <summary>
    /// Correlation id of this request.
    /// <para>本请求的关联 id。</para>
    /// </summary>
    /// <value>
    /// The id, initialized to a freshly generated GUID.
    /// <para>标识符，初始化为新生成的 GUID。</para>
    /// </value>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// The schema presented to the user.
    /// <para>呈现给用户的架构。</para>
    /// </summary>
    /// <value>
    /// The form; the host validates it before showing the request.
    /// <para>表单；宿主在展示请求前先对其进行校验。</para>
    /// </value>
    public required InteractionForm Form { get; init; }

    /// <summary>
    /// Origin text the host appends to the window title.
    /// <para>宿主追加到窗口标题的来源文本。</para>
    /// </summary>
    /// <value>
    /// The source label; empty shows only the form title.
    /// <para>来源标签；为空时仅显示表单标题。</para>
    /// </value>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Optional wait limit; the host rejects a value that is not positive or exceeds <c>uint.MaxValue - 1</c> milliseconds, and expiry ends the request with <see cref="InteractionOutcome.TimedOut"/>.
    /// <para>可选的等待时限；宿主拒绝非正值或超过 <c>uint.MaxValue - 1</c> 毫秒的值，超时后请求以 <see cref="InteractionOutcome.TimedOut"/> 结束。</para>
    /// </summary>
    /// <value>
    /// The duration, or <see langword="null"/> to wait until the user responds or the workflow cancels.
    /// <para>时长；为 <see langword="null"/> 时等待用户响应或工作流取消。</para>
    /// </value>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// Terminal state of one interaction request.
/// <para>单次交互请求的终态。</para>
/// </summary>
public enum InteractionOutcome
{
    /// <summary>
    /// The user submitted a valid form and the result carries its values.
    /// <para>用户提交了合法表单，结果携带其值。</para>
    /// </summary>
    Submitted,
    /// <summary>
    /// The user dismissed the request without submitting; no data is carried.
    /// <para>用户未提交即关闭请求；不携带数据。</para>
    /// </summary>
    Cancelled,
    /// <summary>
    /// The request timeout elapsed before a submission completed; no data is carried.
    /// <para>在提交完成之前请求超时；不携带数据。</para>
    /// </summary>
    TimedOut,
}

/// <summary>
/// Only submitted results carry data. String rendering never reveals form values.
/// <para>仅已提交的结果携带数据。字符串渲染不会暴露表单值。</para>
/// </summary>
public sealed record InteractionResult
{
    /// <summary>
    /// How the interaction ended.
    /// <para>交互结束的方式。</para>
    /// </summary>
    /// <value>
    /// The terminal outcome recorded by the host.
    /// <para>宿主记录的终态。</para>
    /// </value>
    public InteractionOutcome Outcome { get; init; }

    /// <summary>
    /// Submitted values keyed by field id under ordinal comparison; only enabled fields appear.
    /// <para>按字段 id 为键、以序数比较索引的已提交值；仅包含处于启用状态的字段。</para>
    /// </summary>
    /// <value>
    /// The value map, empty by default and carrying data only when <c>Outcome</c> is <see cref="InteractionOutcome.Submitted"/>.
    /// <para>值映射；默认为空，且仅在 <c>Outcome</c> 为 <see cref="InteractionOutcome.Submitted"/> 时携带数据。</para>
    /// </value>
    public IReadOnlyDictionary<string, InteractionValue> Values { get; init; } = new Dictionary<string, InteractionValue>();

    /// <summary>
    /// Renders only the outcome so collected form values never reach string output.
    /// <para>仅渲染结果状态，采集到的表单值不会进入字符串输出。</para>
    /// </summary>
    /// <returns>
    /// The textual name of <see cref="Outcome"/>.
    /// <para><see cref="Outcome"/> 的文本名称。</para>
    /// </returns>
    public override string ToString() => Outcome.ToString();
}