using UotanStudio.ViewModels.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Unit tests for categorized toolbox groups and search behavior in the node editor.
/// <para>节点编辑器中工具箱分类分组与搜索行为的单元测试。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class NodeToolboxGroupTests
{
    public NodeToolboxGroupTests() => NodeFactoryRegistrar.RegisterAll();

    [Fact]
    public void ToolboxGroups_Initialized_ContainsDefinedCategories()
    {
        using var editor = new NodeEditorViewModel();

        Assert.NotEmpty(editor.ToolboxGroups);
        Assert.Equal("Favorites", editor.ToolboxGroups[0].Key);
        Assert.Contains(editor.ToolboxGroups, group => group.Key == "FirmwareConfig");
        Assert.Contains(editor.ToolboxGroups, group => group.Key == "Device");
        Assert.Contains(editor.ToolboxGroups, group => group.Key == "DataFiles");
        Assert.Contains(editor.ToolboxGroups, group => group.Key == "FlowInteraction");
        Assert.Contains(editor.ToolboxGroups, group => group.Key == "Extensions");

        // Verify default expansion: only FirmwareConfig is expanded by default
        var firmwareGroup = Assert.Single(editor.ToolboxGroups, group => group.Key == "FirmwareConfig");
        Assert.True(firmwareGroup.IsExpanded);

        foreach (var group in editor.ToolboxGroups.Where(g => g.Key != "FirmwareConfig"))
        {
            Assert.False(group.IsExpanded);
        }
    }

    [Fact]
    public void ToolboxGroups_Items_DistributedIntoCorrectCategories()
    {
        using var editor = new NodeEditorViewModel();

        var firmwareGroup = Assert.Single(editor.ToolboxGroups, g => g.Key == "FirmwareConfig");
        var deviceGroup = Assert.Single(editor.ToolboxGroups, g => g.Key == "Device");
        var dataGroup = Assert.Single(editor.ToolboxGroups, g => g.Key == "DataFiles");
        var flowGroup = Assert.Single(editor.ToolboxGroups, g => g.Key == "FlowInteraction");

        Assert.Contains(firmwareGroup.Items, item => item.TypeKey == "Firmware.Container.Unpack");
        Assert.Contains(deviceGroup.Items, item => item.TypeKey == "Device.Partition.Flash");
        Assert.Contains(dataGroup.Items, item => item.TypeKey == "File.Binary.Read");
        Assert.Contains(flowGroup.Items, item => item.TypeKey == "Flow.Condition.Branch");
    }

    [Fact]
    public void SearchText_FiltersItems_ExpandsMatchingGroupsAndHidesEmptyGroups()
    {
        using var editor = new NodeEditorViewModel();

        editor.SearchText = "Flash";
        editor.RefreshVisibleToolbox();

        var deviceGroup = Assert.Single(editor.ToolboxGroups, g => g.Key == "Device");
        Assert.True(deviceGroup.IsVisible);
        Assert.True(deviceGroup.IsExpanded);
        Assert.Contains(deviceGroup.Items, item => item.TypeKey == "Device.Partition.Flash");

        // Groups with no matching item should become invisible
        var flowGroup = Assert.Single(editor.ToolboxGroups, g => g.Key == "FlowInteraction");
        Assert.False(flowGroup.IsVisible);
        Assert.Empty(flowGroup.Items);
    }
}