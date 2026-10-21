using System.Diagnostics.CodeAnalysis;
using UotanStudio.Services.Plugins;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Single source of truth for workflow node identity, toolbox metadata and factories.<para>工作流节点身份、工具箱元数据与工厂的唯一事实来源。</para></summary>
public static class NodeFactoryRegistrar
{
    /// <summary>Built-in and enabled visual-plugin descriptors.<para>内置及已启用的可视化插件描述符。</para></summary>
    /// <value>The current descriptor list.<para>当前的描述符列表。</para></value>
    public static IReadOnlyList<NodeDescriptor> Descriptors => PluginRegistry.Shared.Descriptors;

    /// <summary>Stable type keys of the built-in node types.<para>内置节点类型的稳定类型键。</para></summary>
    /// <value>The read-only set of built-in type keys.<para>内置类型键的只读集合。</para></value>
    public static IReadOnlySet<string> BuiltInTypeKeys => PluginRegistry.Shared.BuiltInTypeKeys;

    /// <summary>Raised whenever the set of dynamically registered toolbox nodes changes.<para>动态注册的工具箱节点集合发生变化时触发。</para></summary>
    public static event EventHandler? DescriptorsChanged
    {
        add => PluginRegistry.Shared.DescriptorsChanged += value;
        remove => PluginRegistry.Shared.DescriptorsChanged -= value;
    }

    public static void RegisterAll() => PluginRegistry.Shared.RegisterAll();

    /// <summary>Registers or replaces one composite-node or script-node package in the toolbox.<para>在工具箱中注册或替换一个组合节点或脚本节点软件包。</para></summary>
    /// <param name="package">The composite or script package to register.<para>要注册的组合或脚本软件包。</para></param>
    /// <param name="validateExecution">Optional hook that returns validation errors before execution.<para>可选回调，在执行前返回校验错误。</para></param>
    public static void RegisterVisualPlugin(VisualPluginPackage package, Func<IReadOnlyList<string>>? validateExecution = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var descriptor = package.Kind switch
        {
            VisualPluginKind.CompositeNode => NodeCatalogSeed.CreateCompositePluginDescriptor(package, validateExecution),
            VisualPluginKind.ScriptNode => NodeCatalogSeed.CreateScriptPluginDescriptor(package, validateExecution),
            _ => throw new ArgumentException(
                "Only composite-node or script-node packages can be registered in the toolbox.", nameof(package))
        };
        PluginRegistry.Shared.RegisterNode(descriptor, package);
    }

    /// <summary>Unregisters one visual-plugin node type.<para>注销一个可视化插件节点类型。</para></summary>
    /// <param name="typeKey">Type key of the node to unregister.<para>待注销节点的类型键。</para></param>
    public static void UnregisterVisualPlugin(string typeKey) => PluginRegistry.Shared.UnregisterNode(typeKey);

    internal static bool TryGetVisualPluginPackage(string typeKey, [NotNullWhen(true)] out VisualPluginPackage? package) =>
        PluginRegistry.Shared.TryGetPackage(typeKey, out package);
}

public sealed record NodeDescriptor(
    string TypeKey,
    string NameResourceKey,
    Func<FlowNodeViewModel> Factory,
    NodeCapability Capabilities = NodeCapability.None,
    string? DisplayName = null)
{
    /// <summary>Whether users may create new instances from the toolbox; hidden types remain loadable.<para>用户是否可从工具箱创建新实例；隐藏类型仍可加载。</para></summary>
    /// <value>True when new instances may be created from the toolbox.<para>可从工具箱创建新实例时为 True。</para></value>
    public bool IsToolboxVisible { get; init; } = true;

    /// <summary>Stable presentation category declared per registration; independent of execution capabilities.<para>按注册声明的稳定呈现分类，与执行能力无关。</para></summary>
    /// <value>The presentation category path.<para>呈现用的分类路径。</para></value>
    public required string CategoryPath { get; init; }

    /// <summary>Whether the node mutates device state and therefore needs explicit consent before a run.<para>节点是否会改变设备状态，因而运行前需要明确同意。</para></summary>
    /// <value>True when a run must be confirmed first.<para>运行前必须先确认时为 True。</para></value>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Device protocol states the node can operate in; declared for policy, not enforced while authoring.<para>节点可运行的设备协议状态；按策略声明，编辑期不强制。</para></summary>
    /// <value>The required device state declaration.<para>所要求的设备状态声明。</para></value>
    public DeviceRequiredState RequiredDeviceState { get; init; } = DeviceRequiredState.None;

    /// <summary>
    /// Declared object family for the second toolbox level. Two-segment keys whose second segment names the
    /// operation rather than the object, such as <c>Config.Edit</c>, must declare the object explicitly.
    /// <para>声明的二级工具箱对象族。第二段是动作而非对象名的两段式键（如 <c>Config.Edit</c>）必须显式声明对象。</para>
    /// </summary>
    /// <value>The declared family, or null to derive it from the type key.<para>声明的对象族名；为 null 时由类型键推导。</para></value>
    public string? DeclaredFamily { get; init; }

    /// <summary>Object family naming the second toolbox level.<para>为工具箱第二层级命名的对象族。</para></summary>
    /// <value>The family segment derived from the type key.<para>由类型键推导出的族名段。</para></value>
    public string Family => DeclaredFamily ?? (TypeKey.Split('.') is [_, var family, ..] ? family : TypeKey);

    /// <summary>
    /// Retired type keys merged into this node. Kept as stable search terms so a name that disappeared from the
    /// catalog still resolves to its successor in the toolbox.
    /// <para>已并入本节点的停用类型键，保留为稳定检索词，使已从目录消失的名称仍能在工具箱中命中继任节点。</para>
    /// </summary>
    /// <value>The retired type keys served by this node.<para>由本节点承接的停用类型键。</para></value>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Stable technical search terms augment localized display labels.<para>补充本地化显示标签的稳定技术检索词。</para></summary>
    /// <value>The technical search terms for this node.<para>该节点的技术检索词条。</para></value>
    public IReadOnlyList<string> SearchKeywords => TypeKey switch
    {
        "Binary.Search" => [TypeKey, CategoryPath, Family, "hexpatch", "hex", "binary", .. Aliases],
        "Config.Edit" => [TypeKey, CategoryPath, Family, "build.prop", "property", .. Aliases],
        "Device.Partition.Flash" => [TypeKey, CategoryPath, Family, "boot", "bootloader", "super", "fastboot", .. Aliases],
        _ => [TypeKey, CategoryPath, Family, .. Aliases]
    };
}

[Flags]
public enum DeviceRequiredState
{
    /// <summary>No device transport is required.<para>不需要任何设备传输。</para></summary>
    None = 0,
    /// <summary>ADB (system, recovery or sideload).<para>ADB（system、recovery 或 sideload）。</para></summary>
    Adb = 1 << 0,
    /// <summary>Fastboot (bootloader or fastbootd).<para>Fastboot（bootloader 或 fastbootd）。</para></summary>
    Fastboot = 1 << 1,
    /// <summary>Qualcomm EDL (9008).<para>高通 EDL（9008）。</para></summary>
    Edl = 1 << 2,
    /// <summary>Rockchip bootrom or loader.<para>瑞芯微 bootrom 或 loader。</para></summary>
    Rkdl = 1 << 3,
    /// <summary>MediaTek BootROM or download agent.<para>联发科 BootROM 或下载代理。</para></summary>
    Mtk = 1 << 4,
    /// <summary>OpenHarmony HDC over USB or the local hdc server.<para>经 USB 或本机 hdc server 的 OpenHarmony HDC。</para></summary>
    Hdc = 1 << 5,
    /// <summary>Every supported device transport.<para>所有受支持的设备传输。</para></summary>
    AnyDevice = Adb | Fastboot | Edl | Rkdl | Mtk | Hdc
}

[Flags]
public enum NodeCapability
{
    None = 0,
    ControlFlow = 1 << 0,
    RequiresDevice = 1 << 1,
    ReadsDevice = 1 << 2,
    MutatesDevice = 1 << 3,
    ProducesArtifact = 1 << 4,
    RequiresNetwork = 1 << 5,
    PatchOperation = 1 << 6,
    PatchSafe = 1 << 7,
    StagedWrite = 1 << 8,
    PublishesFile = 1 << 9,
    RequiresInteraction = 1 << 10
}