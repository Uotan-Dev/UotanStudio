using UotanStudio.Services.Devices;
using UotanStudio.Workflow.Registry;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Catalog-independent metadata for one device protocol node: its stable type key, the protocol it operates on,
/// the capabilities it declares, the factory that creates it and the presentation metadata used when the node is
/// registered in the built-in catalog.
/// <para>设备协议节点的目录外元数据：稳定类型键、所操作的协议、声明的能力、创建工厂，以及注册到内置目录时使用的呈现元数据。</para>
/// </summary>
/// <param name="TypeKey">Stable node type key.<para>稳定的节点类型键。</para></param>
/// <param name="RequiredProtocol">Protocol the node operates on, or <see langword="null"/> when any protocol is accepted.<para>节点所操作的协议；接受任意协议时为 <see langword="null"/>。</para></param>
/// <param name="Capabilities">Capabilities the node declares to the engine.<para>节点向引擎声明的能力。</para></param>
/// <param name="Factory">Factory creating the node instance.<para>创建节点实例的工厂。</para></param>
/// <param name="NameResourceKey">Optional locale resource key for the toolbox label; derived from <paramref name="TypeKey"/> when omitted.<para>工具箱标签的可选本地化资源键；省略时由 <paramref name="TypeKey"/> 推导。</para></param>
/// <param name="CategoryPath">Optional toolbox category path; defaults to <c>Device</c>.<para>可选工具箱分类路径；默认为 <c>Device</c>。</para></param>
/// <param name="DisplayName">Optional fixed display name that replaces the localized label.<para>可选的固定显示名称，用于取代本地化标签。</para></param>
public sealed record DeviceProtocolNodeSpec(
    string TypeKey,
    DeviceProtocolKind? RequiredProtocol,
    NodeCapability Capabilities,
    Func<FlowNodeViewModel> Factory,
    string? NameResourceKey = null,
    string? CategoryPath = null,
    string? DisplayName = null)
{
    private const string DefaultCategory = "Device";

    /// <summary>
    /// Locale resource key used for this node's toolbox label.
    /// <para>该节点工具箱标签所用的本地化资源键。</para>
    /// </summary>
    /// <value>The declared key, or <c>Node_</c> plus the type key with dots replaced by underscores.<para>声明的键，或由 <c>Node_</c> 加类型键（点替换为下划线）构成的键。</para></value>
    public string EffectiveNameResourceKey => NameResourceKey ?? "Node_" + TypeKey.Replace('.', '_');

    /// <summary>
    /// Whether a run must obtain explicit user consent first; derived from <see cref="NodeCapability.MutatesDevice"/>.
    /// <para>运行前是否必须获得用户显式同意；由 <see cref="NodeCapability.MutatesDevice"/> 推导。</para>
    /// </summary>
    public bool RequiresConfirmation => Capabilities.HasFlag(NodeCapability.MutatesDevice);

    /// <summary>
    /// Builds the catalog descriptor for this node; registering the returned value is a separate step.
    /// <para>构建该节点的目录描述符；注册返回值是另一个独立步骤。</para>
    /// </summary>
    /// <remarks>
    /// <see cref="RequiredProtocol"/> maps onto the matching <see cref="DeviceRequiredState"/> flag for every
    /// supported protocol, and a protocol-agnostic node maps to <see cref="DeviceRequiredState.AnyDevice"/>.
    /// <para><see cref="RequiredProtocol"/> 对每个受支持协议映射到对应的 <see cref="DeviceRequiredState"/> 标志，
    /// 与协议无关的节点映射到 <see cref="DeviceRequiredState.AnyDevice"/>。</para>
    /// </remarks>
    /// <returns>The descriptor carrying the same key, factory, capabilities, confirmation flag and device state requirement.<para>携带相同类型键、工厂、能力、确认标志与设备状态要求的描述符。</para></returns>
    public NodeDescriptor ToCatalogDescriptor() =>
        new(TypeKey, EffectiveNameResourceKey, Factory, Capabilities, DisplayName)
        {
            CategoryPath = CategoryPath ?? DefaultCategory,
            RequiresConfirmation = RequiresConfirmation,
            RequiredDeviceState = RequiredProtocol switch
            {
                DeviceProtocolKind.Adb => DeviceRequiredState.Adb,
                DeviceProtocolKind.Fastboot => DeviceRequiredState.Fastboot,
                DeviceProtocolKind.Edl => DeviceRequiredState.Edl,
                DeviceProtocolKind.Rkdl => DeviceRequiredState.Rkdl,
                DeviceProtocolKind.Mtk => DeviceRequiredState.Mtk,
                DeviceProtocolKind.Hdc => DeviceRequiredState.Hdc,
                null => DeviceRequiredState.AnyDevice,
                _ => DeviceRequiredState.None
            }
        };
}