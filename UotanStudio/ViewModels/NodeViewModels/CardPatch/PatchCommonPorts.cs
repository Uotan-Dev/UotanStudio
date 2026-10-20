using System.Globalization;
using UotanStudio.Firmware.Patching;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Constant port declarations shared by the patch node family: the ports whose definition never depends on the node
/// that declares them.
/// <para>Patch 节点家族共享的常量端口声明：定义不随声明节点变化的端口。</para>
/// </summary>
/// <remarks>
/// A <see cref="NodeInputDeclaration"/> is located on a node by its title, so sharing one instance across node types
/// is equivalent to redeclaring it. Visibility predicates are the exception: they read the controlling declaration
/// captured at construction, which is why every port that carries a <see cref="NodeInputDeclaration.VisibleWhen"/>
/// rule lives in <see cref="PatchCommonPorts"/> as a factory rather than as a field — a shared instance would bind
/// the rule to the controlling port of whichever node it was built for. Neither an initial value nor a declared
/// <see cref="NodeInputDeclaration.Required"/> flag is mutated while a node is edited, so the instances stay read-only
/// after construction.
/// <para>端口按标题在节点上定位，因此跨节点类型共享同一实例与逐处重新声明等价。例外是可见性谓词：它在构造时捕获被引用的声明对象，所以凡是带 <see cref="NodeInputDeclaration.VisibleWhen"/> 规则的端口都以工厂方法提供而不是字段——共享实例会把规则绑到构造它的那个节点的控制端口上。初始值与声明的 <see cref="NodeInputDeclaration.Required"/> 在编辑节点时都不会被改写，所以实例在构造之后保持只读。</para>
/// </remarks>
internal static class PatchCommonPorts
{
    /// <summary>
    /// Category choices common to every configuration reader: the whole-file view and the on-disk entry kinds.
    /// <para>所有配置读取器共用的类别选项：整文件视图与磁盘上的条目类型。</para>
    /// </summary>
    internal static readonly string[] FileKinds =
        ["Any", "Regular", "Directory", "Symlink", "Block", "Character", "Pipe", "Socket"];

    /// <summary>
    /// Text encodings the patch family can read and write.
    /// <para>Patch 家族可读写的文本编码。</para>
    /// </summary>
    internal static readonly string[] TextEncodings = ["utf8", "utf8-bom", "utf16-le", "utf16-be"];

    /// <summary>
    /// Image file systems the patch family can address.
    /// <para>Patch 家族可寻址的镜像文件系统。</para>
    /// </summary>
    internal static readonly string[] ImageFileSystems = ["EXT4", "EROFS"];

    /// <summary>
    /// Default extraction size budget for one image read, 64 GiB as a decimal digit string.
    /// <para>单次镜像读取的默认解压大小预算，即 64 GiB 的十进制数字串。</para>
    /// </summary>
    internal const string DefaultExtractionBudget = "68719476736";

    /// <summary>
    /// Gets the title of the operation selector, the port most patch nodes branch on; typed once so the
    /// <see cref="TextValue"/> lookup and the factories below cannot drift apart.
    /// <para>获取操作选择器标题，即多数 Patch 节点用来分派的端口；在此唯一定义，避免 <see cref="TextValue"/> 查找与下列工厂方法彼此漂移。</para>
    /// </summary>
    /// <value>The literal <c>Operation</c>. <para>字面量 <c>Operation</c>。</para></value>
    internal const string OperationTitle = "Operation";

    /// <summary>
    /// Gets the title of the operation that replaces content instead of rewriting it wholesale.
    /// <para>获取以内容替换而非整体改写的方式执行的操作标题。</para>
    /// </summary>
    /// <value>The literal <c>Replace</c>. <para>字面量 <c>Replace</c>。</para></value>
    internal const string ReplaceOperation = "Replace";

    /// <summary>
    /// Gets a required image path input.
    /// <para>获取必填的镜像内路径输入。</para>
    /// </summary>
    /// <param name="hint">Optional localized hint resource key. <para>可选的本地化提示资源键。</para></param>
    /// <returns>A fresh declaration, because the hint varies per node. <para>新的声明，因为提示按节点变化。</para></returns>
    internal static NodeInputDeclaration Path(string? hint = null)
        => new("Path", NodePortType.Path) { Required = true, HintResourceKey = hint };

    /// <summary>
    /// Gets a multiline text input.
    /// <para>获取多行文本输入。</para>
    /// </summary>
    /// <param name="hint">Optional localized hint resource key. <para>可选的本地化提示资源键。</para></param>
    /// <returns>A fresh declaration, because the hint varies per node. <para>新的声明，因为提示按节点变化。</para></returns>
    internal static NodeInputDeclaration MultilineText(string? hint = null)
        => new("Text", NodePortType.Text, string.Empty) { Multiline = true, HintResourceKey = hint };

    /// <summary>
    /// Gets a text input with the given title, ordinary or multiline, and an optional hint.
    /// <para>获取具有给定标题的文本输入，单行或多行，并可带提示。</para>
    /// </summary>
    /// <param name="title">Port title. <para>端口标题。</para></param>
    /// <param name="multiline">Whether the editor accepts line breaks. <para>编辑器是否接受换行。</para></param>
    /// <param name="hint">Optional localized hint resource key. <para>可选的本地化提示资源键。</para></param>
    /// <returns>The declaration. <para>声明。</para></returns>
    internal static NodeInputDeclaration Text(string title, bool multiline = false, string? hint = null)
        => new(title, NodePortType.Text, string.Empty) { Multiline = multiline, HintResourceKey = hint };

    /// <summary>
    /// Gets an operation selector over the given choices.
    /// <para>获取在给定选项上取值的操作选择器。</para>
    /// </summary>
    /// <param name="initialValue">Initial selection. <para>初始选中项。</para></param>
    /// <param name="choices">Stable choice list. <para>稳定选项列表。</para></param>
    /// <returns>A fresh declaration, because the operation vocabulary differs per node. <para>新的声明，因为各节点的操作词表不同。</para></returns>
    internal static NodeInputDeclaration Operation(string initialValue, IReadOnlyList<string> choices)
        => new(OperationTitle, NodePortType.Text, initialValue) { Choices = choices };

    /// <summary>
    /// Gets a required workspace input.
    /// <para>获取必填的工作区输入。</para>
    /// </summary>
    /// <returns>The declaration. <para>声明。</para></returns>
    internal static NodeInputDeclaration Workspace()
        => new("Workspace", NodePortType.PatchWorkspace) { Required = true };

    /// <summary>
    /// Gets a required relative path input.
    /// <para>获取必填的相对路径输入。</para>
    /// </summary>
    /// <returns>The declaration. <para>声明。</para></returns>
    internal static NodeInputDeclaration RelativePath()
        => new("Relative Path", NodePortType.Text, string.Empty) { Required = true };

    /// <summary>
    /// Gets the text input available while the node's operation may equal any of the given values.
    /// <para>获取在该节点操作可能等于任一给定值时可用的文本输入。</para>
    /// </summary>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads <paramref name="operation"/>. <para>规则读取 <paramref name="operation"/> 的声明。</para></returns>
    internal static NodeInputDeclaration OperationText(NodeInputDeclaration operation, params string[] values)
        => new("Text", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, values)
        };

    /// <summary>
    /// Gets the text encoding selector available while the node's operation may equal any of the given values.
    /// <para>获取在该节点操作可能等于任一给定值时可用的文本编码选择器。</para>
    /// </summary>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads <paramref name="operation"/>. <para>规则读取 <paramref name="operation"/> 的声明。</para></returns>
    internal static NodeInputDeclaration OperationEncoding(NodeInputDeclaration operation, params string[] values)
        => new("Encoding", NodePortType.Text, "utf8")
        {
            Choices = TextEncodings,
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, values)
        };

    /// <summary>
    /// Gets the expected hash input available while the node's operation may equal any of the given values.
    /// <para>获取在该节点操作可能等于任一给定值时可用的期望哈希输入。</para>
    /// </summary>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads <paramref name="operation"/>. <para>规则读取 <paramref name="operation"/> 的声明。</para></returns>
    internal static NodeInputDeclaration OperationExpectedHash(NodeInputDeclaration operation, params string[] values)
        => new("Expected Hash", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, values)
        };

    /// <summary>
    /// Gets the image capacity input available while the node's operation may equal any of the given values.
    /// <para>获取在该节点操作可能等于任一给定值时可用的镜像容量输入。</para>
    /// </summary>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads <paramref name="operation"/>. <para>规则读取 <paramref name="operation"/> 的声明。</para></returns>
    internal static NodeInputDeclaration OperationImageCapacity(NodeInputDeclaration operation, params string[] values)
        => new("Image Capacity", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            HintResourceKey = "Node_Patch_ImageCapacityHint",
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, values)
        };

    /// <summary>
    /// Gets the extraction budget input available while the node's operation may equal any of the given values.
    /// <para>获取在该节点操作可能等于任一给定值时可用的解压预算输入。</para>
    /// </summary>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads <paramref name="operation"/>. <para>规则读取 <paramref name="operation"/> 的声明。</para></returns>
    internal static NodeInputDeclaration OperationExtractionBudget(NodeInputDeclaration operation, params string[] values)
        => new("Extraction Budget", NodePortType.Text, DefaultExtractionBudget)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, values)
        };

    /// <summary>
    /// Gets the "allow create" switch available while the node's operation may equal any of the given values.
    /// <para>获取在该节点操作可能等于任一给定值时可用的"允许创建"开关。</para>
    /// </summary>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads <paramref name="operation"/>. <para>规则读取 <paramref name="operation"/> 的声明。</para></returns>
    internal static NodeInputDeclaration OperationAllowCreate(NodeInputDeclaration operation, params string[] values)
        => new("Allow Create", NodePortType.Boolean, false)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, values)
        };

    /// <summary>
    /// Gets the "check old value" guard available while the node's operation may equal any of the given values.
    /// <para>获取在该节点操作可能等于任一给定值时可用的"检查旧值"守卫。</para>
    /// </summary>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads <paramref name="operation"/>. <para>规则读取 <paramref name="operation"/> 的声明。</para></returns>
    internal static NodeInputDeclaration OperationCheckOldValue(NodeInputDeclaration operation, params string[] values)
        => new("Check Old Value", NodePortType.Boolean, false)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, values)
        };

    /// <summary>
    /// Gets the expected old value input, visible while the given guard is on and the node's operation may equal any
    /// of the given values.
    /// <para>获取期望旧值输入，在给定守卫开启且该节点操作可能等于任一给定值时可见。</para>
    /// </summary>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="guard">The check-old-value declaration on the same node. <para>同一节点上的检查旧值声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads both controlling declarations. <para>规则读取两个控制声明的声明。</para></returns>
    internal static NodeInputDeclaration GuardedExpectedOldValue(
        NodeInputDeclaration operation,
        NodeInputDeclaration guard,
        params string[] values)
        => new("Expected Old Value", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(guard)(node)
                && NodePortVisibility.MayEqualAny(operation, values)(node)
        };

    /// <summary>
    /// Gets one of the expected ownership/metadata inputs (UID, GID or mode), visible while the given guard is on and
    /// the node's operation may equal any of the given values.
    /// <para>获取期望归属/元数据输入之一（UID、GID 或 Mode），在给定守卫开启且该节点操作可能等于任一给定值时可见。</para>
    /// </summary>
    /// <param name="title">Port title of the expected field. <para>期望字段的端口标题。</para></param>
    /// <param name="initialValue">Default expected value. <para>默认期望值。</para></param>
    /// <param name="operation">The operation declaration on the node that owns the port. <para>拥有该端口的节点上的操作声明。</para></param>
    /// <param name="guard">The check-old-value declaration on the same node. <para>同一节点上的检查旧值声明。</para></param>
    /// <param name="values">The operations that keep the port visible. <para>保持端口可见的操作。</para></param>
    /// <returns>A declaration whose rule reads both controlling declarations. <para>规则读取两个控制声明的声明。</para></returns>
    internal static NodeInputDeclaration GuardedExpectedField(
        string title,
        string initialValue,
        NodeInputDeclaration operation,
        NodeInputDeclaration guard,
        params string[] values)
        => new(title, NodePortType.Text, initialValue)
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(guard)(node)
                && NodePortVisibility.MayEqualAny(operation, values)(node)
        };

    /// <summary>
    /// Reads the constant text currently declared for the port with the given title.
    /// <para>读取给定标题端口上当前声明的常量文本。</para>
    /// </summary>
    /// <param name="node">The node whose inputs are read. <para>被读取输入的节点。</para></param>
    /// <param name="title">Port title to read. <para>要读取的端口标题。</para></param>
    /// <returns>The declared text, or an empty string when no constant is declared. <para>声明的文本；未声明常量时为空字符串。</para></returns>
    internal static string TextValue(FlowNodeViewModel node, string title)
        => Convert.ToString(node.Input.First(input => input.Title == title).InitialValue, CultureInfo.InvariantCulture)
            ?? string.Empty;
}