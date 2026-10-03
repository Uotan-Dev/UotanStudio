using UotanStudio.Mcp.Tools;
using UotanStudio.ViewModels.Nodes;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Pins the declared MCP tool catalog: unique snake_case names, the flash gate confined to the three
/// device-mutating tools, and every declared node counterpart resolving in the node registry.
/// <para>固化声明的 MCP 工具目录：名称唯一且为 snake_case，刷写门控仅限三个改动设备的工具，且每个声明的节点
/// 对应物都能在节点注册表中解析。</para>
/// </summary>
public sealed class PluginToolDescriptorTests
{
    [Fact]
    public void Catalog_Descriptors_AreNamedUniquelyInSnakeCase()
    {
        var names = PluginToolCatalog.All.Select(descriptor => descriptor.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Matches("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", name));
    }

    [Fact]
    public void Catalog_FlashGatedDescriptors_AreExactlyTheDeviceMutatingSet()
    {
        var gated = PluginToolCatalog.All
            .Where(descriptor => descriptor.FlashGated)
            .Select(descriptor => descriptor.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["adb_reboot", "device_shell", "fastboot_erase", "fastboot_flash", "fastboot_slot", "recovery_clean"],
            gated);
        Assert.All(
            PluginToolCatalog.All.Where(descriptor => descriptor.FlashGated),
            descriptor => Assert.True(descriptor.Destructive));
    }

    [Fact]
    public void Catalog_NodeTypeKeys_ResolveInTheNodeCatalog()
    {
        var declared = PluginToolCatalog.All.Where(descriptor => descriptor.NodeTypeKey is not null).ToList();

        Assert.NotEmpty(declared);
        Assert.All(declared, descriptor => Assert.Contains(descriptor.NodeTypeKey!, NodeFactoryRegistrar.BuiltInTypeKeys));
    }
}