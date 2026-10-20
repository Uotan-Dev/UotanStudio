using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Contract of the process-wide resource candidate list a resource identifier port mirrors: publication order,
/// tracking across replacements, and the free-text fallback while nothing has been published.
/// <para>资源标识端口所镜像的进程级候选列表的契约：发布顺序、替换期间的跟踪，以及尚未发布任何候选时的自由文本回退。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class NodeResourceChoiceTests
{
    [Fact]
    public void Apply_AfterResourcesArePublished_TracksThePortThroughLaterReplacements()
    {
        try
        {
            NodePortChoiceSource.SetResources([("beta", "B"), ("alpha", "A"), ("alpha", "Duplicate"), ("", "Blank")]);
            var port = AssetIdPort();

            NodeResourceIdChoices.Apply(port);

            Assert.Equal(["alpha", "beta"], port.Choices.Select(choice => choice.Value));

            NodePortChoiceSource.SetResources([("gamma", "G")]);
            Assert.Equal(["gamma"], port.Choices.Select(choice => choice.Value));

            NodePortChoiceSource.Clear();
            Assert.Empty(port.Choices);
        }
        finally
        {
            NodePortChoiceSource.Clear();
        }
    }

    [Fact]
    public void Apply_BeforeAnyResourceIsPublished_KeepsThePortOnTheTextEditor()
    {
        try
        {
            NodePortChoiceSource.Clear();
            var port = AssetIdPort();

            NodeResourceIdChoices.Apply(port);

            Assert.False(port.HasChoices);
            Assert.True(port.UsesTextEditor);

            NodePortChoiceSource.SetResources([("late", "L")]);
            Assert.False(port.HasChoices);
        }
        finally
        {
            NodePortChoiceSource.Clear();
        }
    }

    private static NodeConnectorViewModel AssetIdPort()
        => new() { Title = "Asset ID", PortType = NodePortType.Text };
}