using System.Diagnostics.CodeAnalysis;
using UotanStudio.Services.Plugins;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Single source of truth for workflow node identity, toolbox metadata and factories.</summary>
public static class NodeFactoryRegistrar
{
    /// <summary>Built-in and enabled visual-plugin descriptors.</summary>
    public static IReadOnlyList<NodeDescriptor> Descriptors => PluginRegistry.Shared.Descriptors;

    /// <summary>Stable type keys provided by the host itself.</summary>
    public static IReadOnlySet<string> BuiltInTypeKeys => PluginRegistry.Shared.BuiltInTypeKeys;

    /// <summary>Raised whenever the set of dynamically registered toolbox nodes changes.</summary>
    public static event EventHandler? DescriptorsChanged
    {
        add => PluginRegistry.Shared.DescriptorsChanged += value;
        remove => PluginRegistry.Shared.DescriptorsChanged -= value;
    }

    public static void RegisterAll() => PluginRegistry.Shared.RegisterAll();

    /// <summary>Registers or replaces one host-rendered composite-node or script-node package.</summary>
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

    /// <summary>Unregisters one visual-plugin node type.</summary>
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
    /// <summary>Whether users may create new instances from the toolbox; hidden types remain loadable.</summary>
    public bool IsToolboxVisible { get; init; } = true;

    /// <summary>Stable presentation category declared per registration; independent of execution capabilities.</summary>
    public required string CategoryPath { get; init; }

    /// <summary>Whether the node mutates device state and therefore needs explicit consent before a run.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Device protocol states the node can operate in; declared for host policy, not enforced while authoring.</summary>
    public DeviceRequiredState RequiredDeviceState { get; init; } = DeviceRequiredState.None;

    /// <summary>Object family used for the second toolbox level.</summary>
    public string Family => TypeKey.Split('.') is [_, var family, ..] ? family : TypeKey;

    /// <summary>Stable technical search terms augment localized display labels.</summary>
    public IReadOnlyList<string> SearchKeywords => TypeKey switch
    {
        "Binary.Pattern.Replace" => [TypeKey, CategoryPath, Family, "hexpatch", "hex", "binary"],
        "Device.Partition.Flash" => [TypeKey, CategoryPath, Family, "boot", "bootloader", "super", "fastboot"],
        "Config.AndroidProperty.Edit" => [TypeKey, CategoryPath, Family, "build.prop", "property"],
        _ => [TypeKey, CategoryPath, Family]
    };
}

[Flags]
public enum DeviceRequiredState
{
    None = 0,
    Adb = 1 << 0,
    Fastboot = 1 << 1,
    AnyDevice = Adb | Fastboot
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