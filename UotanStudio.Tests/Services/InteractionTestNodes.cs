using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

internal static class InteractionTestNodes
{
    internal static InteractionNodeViewModel Create(string typeKey)
    {
        NodeFactoryRegistrar.RegisterAll();
        Assert.True(WorkflowReconstruction.TryCreateNode(
            new NodeDto { TypeKey = typeKey, InstanceId = "node" }, out var node), $"No node was created for '{typeKey}'.");
        return Assert.IsType<InteractionNodeViewModel>(node);
    }
}