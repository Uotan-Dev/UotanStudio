using UotanStudio.Projects;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Verifies that an installed plugin node carries the same policy surface as a built-in node.<para>验证已安装插件节点具备与内置节点相同的策略面。</para></summary>
public sealed class PluginDescriptorParityTests
{
    [Fact]
    public void CompositePlugin_DeclaringMutatesDevice_RequiresConfirmation()
    {
        var package = Package("tests.parity.flash", ["RequiresDevice", "MutatesDevice"]);

        var descriptor = NodeCatalogSeed.CreateCompositePluginDescriptor(package, null);

        Assert.True(descriptor.RequiresConfirmation);
        Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.MutatesDevice));
        Assert.Equal(DeviceRequiredState.AnyDevice, descriptor.RequiredDeviceState);
    }

    [Fact]
    public void ScriptPlugin_DeclaringReadOnlyCapabilities_DoesNotRequireConfirmation()
    {
        var package = Package("tests.parity.read", ["RequiresDevice", "ReadsDevice"]);

        var descriptor = NodeCatalogSeed.CreateScriptPluginDescriptor(package, null);

        Assert.False(descriptor.RequiresConfirmation);
        Assert.False(descriptor.Capabilities.HasFlag(NodeCapability.MutatesDevice));
        Assert.Equal(DeviceRequiredState.AnyDevice, descriptor.RequiredDeviceState);
    }

    [Fact]
    public void ScriptPlugin_WithoutRequiresDevice_DeclaresNoDeviceState()
    {
        var package = Package("tests.parity.nodeweight", ["ReadsDevice"]);

        var descriptor = NodeCatalogSeed.CreateScriptPluginDescriptor(package, null);

        Assert.Equal(DeviceRequiredState.None, descriptor.RequiredDeviceState);
        Assert.False(descriptor.RequiresConfirmation);
    }

    [Fact]
    public void PluginDescriptor_ManifestName_IsSearchableAlias()
    {
        var package = Package("tests.parity.alias", []);

        var descriptor = NodeCatalogSeed.CreateCompositePluginDescriptor(package, null);

        Assert.Contains(package.Manifest.Name, descriptor.Aliases);
        Assert.Contains(package.Manifest.Name, descriptor.SearchKeywords);
    }

    [Fact]
    public void PluginDescriptor_KeepsItsOwnCategory()
    {
        var composite = NodeCatalogSeed.CreateCompositePluginDescriptor(Package("tests.parity.c", []), null);
        var script = NodeCatalogSeed.CreateScriptPluginDescriptor(Package("tests.parity.s", []), null);

        Assert.Equal("Composite", composite.CategoryPath);
        Assert.Equal("Script", script.CategoryPath);
    }

    [Fact]
    public void PluginDescriptor_WithoutDeviceCapability_DeclaresNoDeviceState()
    {
        var descriptor = NodeCatalogSeed.CreateCompositePluginDescriptor(Package("tests.parity.plain", []), null);

        Assert.Equal(DeviceRequiredState.None, descriptor.RequiredDeviceState);
        Assert.False(descriptor.RequiresConfirmation);
    }

    [Fact]
    public void BuiltInMutatingNode_AndPluginMutatingNode_ShareConfirmationPolicy()
    {
        var builtIn = NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Device.Partition.Flash");
        var plugin = NodeCatalogSeed.CreateCompositePluginDescriptor(
            Package("tests.parity.compare", ["RequiresDevice", "MutatesDevice"]), null);

        Assert.True(builtIn.RequiresConfirmation);
        Assert.Equal(builtIn.RequiresConfirmation, plugin.RequiresConfirmation);
        Assert.True(plugin.RequiredDeviceState.HasFlag(DeviceRequiredState.AnyDevice));
    }

    private static VisualPluginPackage Package(string id, List<string> capabilities) => new()
    {
        Kind = VisualPluginKind.CompositeNode,
        Manifest = new VisualPluginManifest
        {
            Id = id,
            Name = "Parity Plugin",
            Capabilities = capabilities
        }
    };
}