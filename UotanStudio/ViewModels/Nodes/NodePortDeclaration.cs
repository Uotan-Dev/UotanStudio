using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// <para>声明式输入端口定义，是端口标题、类型、初始值、编辑行为与可见性规则的唯一来源；执行代码按标题读取端口，声明侧改名会立即体现在节点构造中。</para>
/// Declarative input port definition; the single source of truth for the port title, type, initial value, editing
/// behavior and visibility rule. Execution code reads ports by title, so declaration-side renames flow into node
/// construction immediately.
/// </summary>
/// <param name="Title">
/// <para>端口标题，同时是执行代码读取输入与序列化持久化的稳定标识。</para>
/// Port title; also the stable identifier used by execution reads and workflow persistence.
/// </param>
/// <param name="PortType">
/// <para>端口类型，约束连接兼容性与面板编辑器。</para>
/// Port type constraining connection compatibility and the panel editor.
/// </param>
/// <param name="InitialValue">
/// <para>未连接上游时使用的常量初值。</para>
/// Constant initial value used while no upstream source is connected.
/// </param>
/// <param name="Multiline">
/// <para>是否使用多行文本编辑器。</para>
/// Whether the text editor accepts line breaks.
/// </param>
/// <param name="Directory">
/// <para>路径浏览是否只允许选择文件夹。</para>
/// Whether path browsing must select a directory.
/// </param>
/// <param name="Choices">
/// <para>稳定选项列表，非空时面板渲染选择编辑器。</para>
/// Stable choice list; when non-empty the panel renders a selection editor.
/// </param>
/// <param name="HintResourceKey">
/// <para>可选的本地化提示资源键。</para>
/// Optional localized hint resource key.
/// </param>
/// <param name="Required">
/// <para>执行前校验是否要求该输入有值。</para>
/// Whether preflight requires this input to carry a value.
/// </param>
/// <param name="Advanced">
/// <para>是否默认隐藏、仅在开启高级参数后显示。</para>
/// Whether the port stays hidden until advanced parameters are requested.
/// </param>
/// <param name="IsConfiguration">
/// <para>是否为配置操作而非资源承载端口。</para>
/// Whether the port configures an operation rather than carrying a resource.
/// </param>
/// <param name="VisibleWhen">
/// <para>可选的可见性谓词，返回 false 时端口在面板与画布上隐藏但保留持久化值。</para>
/// Optional visibility predicate; when it returns false the port is hidden from the panel and canvas while persisted values remain intact.
/// </param>
public sealed record NodeInputDeclaration(
    string Title,
    NodePortType PortType,
    object? InitialValue = null,
    bool Multiline = false,
    bool Directory = false,
    IReadOnlyList<string>? Choices = null,
    string? HintResourceKey = null,
    bool Required = false,
    bool Advanced = false,
    bool IsConfiguration = false,
    Func<FlowNodeViewModel, bool>? VisibleWhen = null);

/// <summary>
/// <para>声明式输出端口定义，是输出端口标题、类型与取值本地化前缀的唯一来源。</para>
/// Declarative output port definition; the single source of truth for the output title, type and value localization prefix.
/// </summary>
/// <param name="Title">
/// <para>端口标题，执行代码按它写入结果。</para>
/// Port title; execution code writes results under it.
/// </param>
/// <param name="PortType">
/// <para>端口类型，约束连接兼容性。</para>
/// Port type constraining connection compatibility.
/// </param>
/// <param name="ValueResourcePrefix">
/// <para>可选的取值本地化前缀，非空时输出字符串按 <c>前缀 + 值</c> 查找本地化文本。</para>
/// Optional localization prefix; when set, string outputs resolve localized text as <c>prefix + value</c>.
/// </param>
public sealed record NodeOutputDeclaration(
    string Title,
    NodePortType PortType,
    string? ValueResourcePrefix = null);

/// <summary>
/// <para>一个节点类型的完整端口声明：输入与输出按构造顺序排列，<see cref="CompletesFlow"/> 控制是否追加标准 Completed 流输出。</para>
/// Complete port declaration for one node type: inputs and outputs in construction order, with
/// <see cref="CompletesFlow"/> controlling whether the standard Completed flow output is appended.
/// </summary>
/// <param name="Inputs">
/// <para>按构造顺序排列的输入端口声明，排列在基类 Flow 输入之后。</para>
/// Input declarations in construction order, placed after the base Flow input.
/// </param>
/// <param name="Outputs">
/// <para>按构造顺序排列的输出端口声明，排列在标准 Completed 输出之后。</para>
/// Output declarations in construction order, placed after the standard Completed output.
/// </param>
public sealed record NodePortSet(
    IReadOnlyList<NodeInputDeclaration> Inputs,
    IReadOnlyList<NodeOutputDeclaration> Outputs)
{
    /// <summary>
    /// <para>是否在声明输出之后追加标准 Completed 流输出；分支类节点以自有 Flow 输出承载流程时关闭。</para>
    /// Whether the standard Completed flow output is appended after the declared outputs; off for branch nodes that carry flow through their own Flow outputs.
    /// </summary>
    public bool CompletesFlow { get; init; } = true;
}