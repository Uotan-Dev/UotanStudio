namespace UotanStudio.Services.Projects.Schemes;

/// <summary>
/// Execution kind declared by a scheme stage; the runner reports it and never rolls a device stage back.
/// <para>Scheme 阶段声明的执行类型；运行器只报告该类型，从不对设备阶段执行回滚。</para>
/// </summary>
public enum SchemeStageKind
{
    /// <summary>Stage without a specialised meaning.<para>没有专门含义的阶段。</para></summary>
    Generic,

    /// <summary>Stage that owns patch writes.<para>承担补丁写入的阶段。</para></summary>
    Patch,

    /// <summary>Stage that talks to a device; its effects are reported, never rolled back.<para>与设备通信的阶段；其影响只被报告，不会被回滚。</para></summary>
    Device,

    /// <summary>Stage that verifies earlier effects.<para>校验先前影响的阶段。</para></summary>
    Verify
}

/// <summary>
/// How the runner reacts when a stage does not succeed.
/// <para>阶段未成功时运行器的反应方式。</para>
/// </summary>
public enum SchemeStageFailurePolicy
{
    /// <summary>Stop the scheme and leave earlier stage artifacts in place.<para>停止 Scheme 并保留先前阶段的产物。</para></summary>
    Stop,

    /// <summary>Record the failure and continue with the next stage.<para>记录失败并继续执行下一阶段。</para></summary>
    Continue
}

/// <summary>
/// One ordered scheme stage, carrying either an embedded graph or a project-workflow reference.
/// <para>一个有序的 Scheme 阶段，携带内嵌图或项目工作流引用。</para>
/// </summary>
public sealed class SchemeStage
{
    /// <summary>
    /// Stable stage identifier.
    /// <para>稳定的阶段标识符。</para>
    /// </summary>
    /// <value>Identifier unique inside the owning scheme.<para>所属 Scheme 内唯一的标识符。</para></value>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Optional display name of the stage.
    /// <para>可选阶段显示名称。</para>
    /// </summary>
    /// <value>The display name, or <see langword="null"/> when the author supplied none.
    /// <para>显示名称；作者未提供时为 <see langword="null"/>。</para></value>
    public string? Name { get; set; }

    /// <summary>
    /// Declared execution kind of the stage.
    /// <para>阶段声明的执行类型。</para>
    /// </summary>
    /// <value>One of the <see cref="SchemeStageKind"/> values; defaults to <see cref="SchemeStageKind.Generic"/>.
    /// <para><see cref="SchemeStageKind"/> 中的一个值；默认是 <see cref="SchemeStageKind.Generic"/>。</para></value>
    public SchemeStageKind Kind { get; set; } = SchemeStageKind.Generic;

    /// <summary>
    /// Embedded workflow graph executed by this stage.
    /// <para>本阶段执行的内嵌工作流图。</para>
    /// </summary>
    /// <value>The embedded graph; an empty graph when the stage references a project workflow instead.
    /// <para>内嵌图；阶段改为引用项目工作流时为空图。</para></value>
    public WorkflowSnapshot Graph { get; set; } = new();

    /// <summary>
    /// Optional name of a project workflow resolved by the caller instead of <see cref="Graph"/>.
    /// <para>可选的项目工作流名称，由调用方解析以替代 <see cref="Graph"/>。</para>
    /// </summary>
    /// <value>The workflow name, or <see langword="null"/> when the stage runs its embedded graph.
    /// <para>工作流名称；阶段运行内嵌图时为 <see langword="null"/>。</para></value>
    public string? ProjectWorkflowName { get; set; }

    /// <summary>
    /// Declared bindings that feed values into this stage's node input ports.
    /// <para>向本阶段节点输入端口提供取值的声明式绑定。</para>
    /// </summary>
    /// <value>One binding per fed input; empty when the stage needs no external value.
    /// <para>每个被提供的输入一条绑定；阶段不需要外部取值时为空列表。</para></value>
    public List<SchemeStageInputBinding> Inputs { get; set; } = [];

    /// <summary>
    /// Declared bindings that expose node output values to later stages.
    /// <para>把节点输出取值暴露给后续阶段的声明式绑定。</para>
    /// </summary>
    /// <value>One binding per exposed output; empty when the stage exposes nothing.
    /// <para>每个被暴露的输出一条绑定；阶段不暴露任何输出时为空列表。</para></value>
    public List<SchemeStageOutputBinding> Outputs { get; set; } = [];

    /// <summary>
    /// Reaction when this stage does not succeed.
    /// <para>本阶段未成功时的反应方式。</para>
    /// </summary>
    /// <value>The configured policy; defaults to <see cref="SchemeStageFailurePolicy.Stop"/>.
    /// <para>已配置的策略；默认是 <see cref="SchemeStageFailurePolicy.Stop"/>。</para></value>
    public SchemeStageFailurePolicy FailurePolicy { get; set; } = SchemeStageFailurePolicy.Stop;
}

/// <summary>
/// Declarative binding feeding one node input port of a stage.
/// <para>为阶段某个节点输入端口提供取值的声明式绑定。</para>
/// </summary>
public sealed class SchemeStageInputBinding
{
    /// <summary>
    /// Scheme input id this binding consumes when no stage output or literal is named.
    /// <para>未指定阶段输出或字面量时本绑定消费的 Scheme 输入 ID。</para>
    /// </summary>
    /// <value>The declared input id; empty when the binding only forward-references a stage output.
    /// <para>声明的输入 ID；仅前向引用阶段输出时为空字符串。</para></value>
    public string InputId { get; set; } = string.Empty;

    /// <summary>
    /// Optional id of an earlier stage supplying the value.
    /// <para>可选的、提供该取值的先前阶段 ID。</para>
    /// </summary>
    /// <value>The source stage id, or <see langword="null"/> when the value comes from a scheme input.
    /// <para>来源阶段 ID；取值来自 Scheme 输入时为 <see langword="null"/>。</para></value>
    public string? SourceStageId { get; set; }

    /// <summary>
    /// Output id declared by <see cref="SourceStageId"/> and consumed by this binding.
    /// <para>由 <see cref="SourceStageId"/> 声明并被本绑定消费的输出 ID。</para>
    /// </summary>
    /// <value>The source output id, or <see langword="null"/> to reuse <see cref="InputId"/>.
    /// <para>来源输出 ID；为 <see langword="null"/> 时复用 <see cref="InputId"/>。</para></value>
    public string? SourceOutput { get; set; }

    /// <summary>
    /// Literal override value that wins over every other source.
    /// <para>优先于其他所有来源的字面量覆盖值。</para>
    /// </summary>
    /// <value>The literal text, or <see langword="null"/> when the value is resolved dynamically.
    /// <para>字面量文本；取值动态解析时为 <see langword="null"/>。</para></value>
    public string? Literal { get; set; }

    /// <summary>
    /// Instance id of the node whose input port receives the value.
    /// <para>接收该取值的节点实例 ID。</para>
    /// </summary>
    /// <value>The serialized node instance id inside the stage graph.<para>阶段图内序列化的节点实例 ID。</para></value>
    public string NodeInstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Stable input port id, preferred over <see cref="PortTitle"/>.
    /// <para>稳定的输入端口 ID，优先于 <see cref="PortTitle"/>。</para>
    /// </summary>
    /// <value>The stable port id, or <see langword="null"/> when only a title is known.
    /// <para>稳定端口 ID；仅知道标题时为 <see langword="null"/>。</para></value>
    public string? PortId { get; set; }

    /// <summary>
    /// Input port title used when <see cref="PortId"/> is absent.
    /// <para>缺少 <see cref="PortId"/> 时使用的输入端口标题。</para>
    /// </summary>
    /// <value>The port title, or <see langword="null"/> when the port is identified by id.
    /// <para>端口标题；端口以 ID 标识时为 <see langword="null"/>。</para></value>
    public string? PortTitle { get; set; }
}

/// <summary>
/// Declarative binding exposing one node output value to later stages.
/// <para>把某个节点输出取值暴露给后续阶段的声明式绑定。</para>
/// </summary>
public sealed class SchemeStageOutputBinding
{
    /// <summary>
    /// Identifier later stages use to consume this output.
    /// <para>后续阶段消费该输出时使用的标识符。</para>
    /// </summary>
    /// <value>The output id unique inside the owning stage.<para>所属阶段内唯一的输出 ID。</para></value>
    public string OutputId { get; set; } = string.Empty;

    /// <summary>
    /// Instance id of the node owning the exposed output port.
    /// <para>拥有被暴露输出端口的节点实例 ID。</para>
    /// </summary>
    /// <value>The serialized node instance id inside the stage graph.<para>阶段图内序列化的节点实例 ID。</para></value>
    public string NodeInstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Stable output port id, preferred over <see cref="PortTitle"/>.
    /// <para>稳定的输出端口 ID，优先于 <see cref="PortTitle"/>。</para>
    /// </summary>
    /// <value>The stable port id, or <see langword="null"/> when only a title is known.
    /// <para>稳定端口 ID；仅知道标题时为 <see langword="null"/>。</para></value>
    public string? PortId { get; set; }

    /// <summary>
    /// Output port title used when <see cref="PortId"/> is absent.
    /// <para>缺少 <see cref="PortId"/> 时使用的输出端口标题。</para>
    /// </summary>
    /// <value>The port title, or <see langword="null"/> when the port is identified by id.
    /// <para>端口标题；端口以 ID 标识时为 <see langword="null"/>。</para></value>
    public string? PortTitle { get; set; }
}