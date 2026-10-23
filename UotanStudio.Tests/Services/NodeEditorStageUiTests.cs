using UotanStudio.Services;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Stage editing and stage-aware graph validation exposed by the node editor view model.
/// <para>节点编辑器视图模型提供的阶段编辑与阶段感知图校验。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class NodeEditorStageUiTests
{
    public NodeEditorStageUiTests() => NodeFactoryRegistrar.RegisterAll();

    [Fact]
    public void SelectedExecutionStage_SingleSelection_UpdatesStageBadgeAndUndo()
    {
        using var editor = new NodeEditorViewModel();
        var node = NewNode("first");
        editor.AddNode(node);
        editor.SetSelection([node]);

        editor.SelectedExecutionStage = 3;

        Assert.Equal(3, node.ExecutionStage);
        Assert.Equal("S3", node.ExecutionStageBadge);
        Assert.Equal(3, editor.SelectedExecutionStage);
        Assert.Equal("S3", editor.SelectedExecutionStageLabel);
        Assert.Contains("S3", editor.ExecutionStageLabels);

        editor.Undo();

        Assert.Equal(0, node.ExecutionStage);
        Assert.Equal("S0", node.ExecutionStageBadge);
        Assert.Equal("S0", editor.SelectedExecutionStageLabel);
    }

    [Fact]
    public void SelectedExecutionStageLabel_ValidLabel_UpdatesSelectedStage()
    {
        using var editor = new NodeEditorViewModel();
        var node = NewNode("first");
        editor.AddNode(node);
        editor.SetSelection([node]);

        editor.SelectedExecutionStageLabel = "S5";

        Assert.Equal(5, node.ExecutionStage);
        Assert.Equal("S5", editor.SelectedExecutionStageLabel);
    }

    [Fact]
    public void SelectedExecutionStage_MultipleSelection_AppliesToEverySelectedNode()
    {
        using var editor = new NodeEditorViewModel();
        var first = NewNode("first");
        var second = NewNode("second");
        editor.AddNode(first);
        editor.AddNode(second);
        second.ExecutionStage = 2;
        editor.SetSelection([first, second]);

        Assert.True(editor.IsExecutionStageMixed);

        editor.SelectedExecutionStage = 4;

        Assert.Equal(4, first.ExecutionStage);
        Assert.Equal(4, second.ExecutionStage);
        Assert.False(editor.IsExecutionStageMixed);
        Assert.Equal("S4", editor.SelectedExecutionStageLabel);
    }

    [Fact]
    public void SelectedExecutionStage_AboveDefaultRange_ExtendsStageLabels()
    {
        using var editor = new NodeEditorViewModel();
        var node = NewNode("first");
        editor.AddNode(node);
        editor.SetSelection([node]);

        editor.SelectedExecutionStage = 9;

        Assert.Equal(9, node.ExecutionStage);
        Assert.Equal(9, editor.MaximumExecutionStage);
        Assert.Contains("S9", editor.ExecutionStageLabels);
    }

    [Fact]
    public void ValidateGraph_PatchThenDeviceInSeparateStages_ReportsValid()
    {
        using var editor = new NodeEditorViewModel();
        var patch = PatchNodeViewModel.Create("Image.Text.Edit");
        FillRequiredInputs(patch);
        var device = DeviceNode();
        FillRequiredInputs(device);
        device.ExecutionStage = 1;
        editor.AddNode(patch);
        editor.AddNode(device);

        editor.ValidateGraphCommand.Execute(null);

        Assert.Equal(LanguageService.Instance.GetString("Node_Graph_Valid"), editor.ExecutionStatus);
        Assert.Empty(editor.FailedNodes);
        Assert.False(patch.HasValidationError);
        Assert.False(device.HasValidationError);
    }

    [Fact]
    public void ValidateGraph_PatchAndDeviceInOneStage_ReportsUnsupportedStage()
    {
        using var editor = new NodeEditorViewModel();
        var patch = PatchNodeViewModel.Create("Image.Text.Edit");
        FillRequiredInputs(patch);
        var device = DeviceNode();
        FillRequiredInputs(device);
        editor.AddNode(patch);
        editor.AddNode(device);

        editor.ValidateGraphCommand.Execute(null);

        var unsupported = LanguageService.Instance.GetString("Node_Patch_UnsupportedGraph");
        Assert.Contains(unsupported, editor.ExecutionStatus);
        Assert.Contains("S0", editor.ExecutionStatus);
        Assert.Contains(patch, editor.FailedNodes);
        Assert.Contains(device, editor.FailedNodes);
        Assert.Contains(unsupported, patch.ValidationMessage ?? string.Empty);
        Assert.Contains(unsupported, device.ValidationMessage ?? string.Empty);
    }

    [Fact]
    public void ValidateGraph_ConnectionAcrossStages_ReportsStageMismatchOnTarget()
    {
        using var editor = new NodeEditorViewModel();
        var first = NewNode("first", 0);
        var second = NewNode("second", 1);
        editor.AddNode(first);
        editor.AddNode(second);
        editor.CreateConnection(first.Output[0], second.Input[0]);

        editor.ValidateGraphCommand.Execute(null);

        Assert.Contains(second, editor.FailedNodes);
        var message = second.ValidationMessage ?? string.Empty;
        Assert.Contains("S0", message);
        Assert.Contains("S1", message);
        Assert.Contains(first.DisplayHeader, message);
        Assert.Contains(second.DisplayHeader, message);
        Assert.Contains(LanguageService.Instance.GetString("Node_Choice_Stage"), editor.ExecutionStatus);
    }

    private static FlowNodeViewModel NewNode(string title, int stage = 0)
    {
        var node = new FlowNodeViewModel(title, NodeCategory.Data)
        {
            ExecutionStage = stage
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "In", PortId = "in", PortType = NodePortType.Any });
        node.Output.Add(new NodeConnectorViewModel { Title = "Out", PortId = "out", PortType = NodePortType.Any });
        return node;
    }

    private static FlowNodeViewModel DeviceNode()
        => NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Device.Partition.Flash").Factory();

    private static void FillRequiredInputs(FlowNodeViewModel node)
    {
        foreach (var input in node.Input)
        {
            if (input.IsRequired && input.PortType != NodePortType.Flow
                && (input.InitialValue is null || input.InitialValue is string text && string.IsNullOrWhiteSpace(text)))
            {
                input.InitialValue = "value";
            }
        }
    }
}