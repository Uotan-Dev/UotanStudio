using UotanStudio.Services.Plugins;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Pins the shared registration surface: built-in and plugin descriptors are enumerated, ordered,
/// replaced and removed by one implementation, and the registry is readable without constructing the
/// plugin manager.
/// <para>固化共享注册面：内置与插件描述符由同一实现枚举、排序、替换与移除，且注册表无需构造插件管理器即可读取。</para>
/// </summary>
public sealed class PluginRegistryTests
{
    private static readonly string Prefix = "tests.pluginregistry." + Guid.NewGuid().ToString("N") + ".";

    [Fact]
    public void Descriptors_EveryBuiltInTypeKey_AppearsExactlyOnce()
    {
        var descriptors = PluginRegistry.Shared.Descriptors;

        foreach (var typeKey in PluginRegistry.Shared.BuiltInTypeKeys)
        {
            Assert.Single(descriptors, descriptor => descriptor.TypeKey == typeKey);
        }
    }

    [Fact]
    public void Shared_WithoutExplicitInitialization_ExposesBuiltInCatalog()
    {
        Assert.Contains("File.Binary.Read", PluginRegistry.Shared.BuiltInTypeKeys);
        Assert.NotEmpty(PluginRegistry.Shared.Descriptors);
    }

    [Fact]
    public void Descriptors_BuiltInsComeFirstThenPluginsOrderedByDisplayName()
    {
        var registry = new PluginRegistry([Descriptor(Prefix + "builtin.one"), Descriptor(Prefix + "builtin.two")]);
        try
        {
            registry.RegisterNode(Descriptor(Prefix + "plugin.zeta", "Zeta"));
            registry.RegisterNode(Descriptor(Prefix + "plugin.alpha", "Alpha"));

            Assert.Equal(
                [Prefix + "builtin.one", Prefix + "builtin.two", Prefix + "plugin.alpha", Prefix + "plugin.zeta"],
                registry.Descriptors.Select(descriptor => descriptor.TypeKey));
        }
        finally
        {
            registry.UnregisterNode(Prefix + "plugin.zeta");
            registry.UnregisterNode(Prefix + "plugin.alpha");
        }
    }

    [Fact]
    public void RegisterNode_DuplicateTypeKey_ReplacesWithoutDuplicating()
    {
        var registry = new PluginRegistry([Descriptor(Prefix + "builtin.one")]);
        try
        {
            registry.RegisterNode(Descriptor(Prefix + "plugin.x", "First"));
            registry.RegisterNode(Descriptor(Prefix + "plugin.x", "Second"));

            var descriptor = Assert.Single(registry.Descriptors, item => item.TypeKey == Prefix + "plugin.x");
            Assert.Equal("Second", descriptor.DisplayName);
        }
        finally
        {
            registry.UnregisterNode(Prefix + "plugin.x");
        }
    }

    [Fact]
    public void UnregisterNode_RemovesDescriptorAndPackage_KeepsBuiltInKeysStable()
    {
        var registry = new PluginRegistry([Descriptor(Prefix + "builtin.one")]);
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = Prefix + "package", Name = "Registry package" }
        };
        registry.RegisterNode(Descriptor(package.NodeTypeKey), package);

        Assert.True(registry.TryGetPackage(package.NodeTypeKey, out var bound));
        Assert.Same(package, bound);
        Assert.True(registry.UnregisterNode(package.NodeTypeKey));

        Assert.False(registry.TryGetPackage(package.NodeTypeKey, out _));
        Assert.DoesNotContain(registry.Descriptors, item => item.TypeKey == package.NodeTypeKey);
        Assert.Contains(Prefix + "builtin.one", registry.BuiltInTypeKeys);
        Assert.False(registry.UnregisterNode(package.NodeTypeKey));
    }

    [Fact]
    public void RegisterNode_WithoutPackage_LeavesNoPackageBinding()
    {
        var registry = new PluginRegistry([Descriptor(Prefix + "builtin.one")]);
        try
        {
            registry.RegisterNode(Descriptor(Prefix + "plugin.host"));

            Assert.False(registry.TryGetPackage(Prefix + "plugin.host", out _));
        }
        finally
        {
            registry.UnregisterNode(Prefix + "plugin.host");
        }
    }

    [Fact]
    public void DescriptorsChanged_RegisterAndUnregister_RaiseOncePerChange()
    {
        var registry = new PluginRegistry([Descriptor(Prefix + "builtin.one")]);
        var raised = 0;
        registry.DescriptorsChanged += (_, _) => raised++;
        try
        {
            registry.RegisterNode(Descriptor(Prefix + "plugin.x"));
            registry.UnregisterNode(Prefix + "plugin.x");
            registry.UnregisterNode(Prefix + "plugin.missing");

            Assert.Equal(2, raised);
        }
        finally
        {
            registry.UnregisterNode(Prefix + "plugin.x");
        }
    }

    private static NodeDescriptor Descriptor(string typeKey, string? displayName = null) =>
        new(typeKey, string.Empty, UnusedFactory, DisplayName: displayName)
        {
            CategoryPath = "Tests"
        };

    private static FlowNodeViewModel UnusedFactory() => throw new NotSupportedException();
}