using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Contract of the process-wide resource candidate list a resource identifier port mirrors: publication order,
/// tracking across replacements, owner scoping, and the free-text fallback while nothing has been published.
/// <para>资源标识端口所镜像的进程级候选列表的契约：发布顺序、替换期间的跟踪、owner 作用域，以及尚未发布任何候选时的自由文本回退。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class NodeResourceChoiceTests
{
    [Fact]
    public void Apply_AfterResourcesArePublished_TracksThePortAndLabelsChoicesWithResourceNames()
    {
        try
        {
            NodePortChoiceSource.SetResources([("beta", "beta.bin"), ("alpha", "alpha.bin"), ("alpha", "duplicate.bin"), ("", "blank.bin")]);
            var port = AssetIdPort();

            NodeResourceIdChoices.Apply(port);

            Assert.Equal(["alpha", "beta"], port.Choices.Select(choice => choice.Value));
            Assert.Equal(["alpha.bin", "beta.bin"], port.Choices.Select(choice => choice.DisplayName));

            NodePortChoiceSource.SetResources([("gamma", "gamma.bin")]);
            Assert.Equal(["gamma"], port.Choices.Select(choice => choice.Value));
            Assert.Equal(["gamma.bin"], port.Choices.Select(choice => choice.DisplayName));

            NodePortChoiceSource.Clear();
            Assert.Empty(port.Choices);
        }
        finally
        {
            NodePortChoiceSource.Clear();
        }
    }

    [Fact]
    public void Apply_BeforeAnyResourceIsPublished_TracksThePortForLateCandidates()
    {
        try
        {
            NodePortChoiceSource.Clear();
            var port = AssetIdPort();

            NodeResourceIdChoices.Apply(port);

            Assert.False(port.HasChoices);
            Assert.True(port.UsesTextEditor);

            NodePortChoiceSource.SetResources([("late", "late.bin")]);

            Assert.True(port.HasChoices);
            Assert.False(port.UsesTextEditor);
            Assert.Equal(["late"], port.Choices.Select(choice => choice.Value));
            Assert.Equal("late.bin", port.Choices[0].DisplayName);
        }
        finally
        {
            NodePortChoiceSource.Clear();
        }
    }

    [Fact]
    public void SetResourcesAndClear_DifferentOwner_KeepTheCurrentOwnersCandidates()
    {
        var first = new object();
        var second = new object();
        try
        {
            NodePortChoiceSource.SetResources(first, [("alpha", "alpha.bin")]);
            var port = AssetIdPort();
            NodeResourceIdChoices.Apply(port);

            NodePortChoiceSource.SetResources(second, [("beta", "beta.bin")]);
            NodePortChoiceSource.Clear(second);

            Assert.Equal(["alpha"], port.Choices.Select(choice => choice.Value));

            NodePortChoiceSource.Clear(first);
            Assert.Empty(port.Choices);

            NodePortChoiceSource.SetResources(second, [("beta", "beta.bin")]);
            Assert.Equal(["beta"], port.Choices.Select(choice => choice.Value));
        }
        finally
        {
            NodePortChoiceSource.Clear();
        }
    }

    private static NodeConnectorViewModel AssetIdPort()
        => new() { Title = "Asset ID", PortType = NodePortType.Text };
}