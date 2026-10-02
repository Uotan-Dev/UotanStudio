using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchNodePortDeclarationTests
{
    [Fact]
    public void PatchDescriptors_DeclaredPortSets_MatchRegisteredFactories()
    {
        var patchNodes = NodeFactoryRegistrar.Descriptors
            .Select(descriptor => descriptor.Factory())
            .OfType<PatchNodeViewModel>()
            .ToList();

        Assert.Equal(47, patchNodes.Count);
        Assert.All(patchNodes, node =>
        {
            Assert.Equal("Flow", node.Input[0].Title);
            foreach (var port in node.Input.Concat(node.Output))
            {
                Assert.False(string.IsNullOrWhiteSpace(port.PortId));
            }
        });
    }

    [Fact]
    public void PatchDescriptors_DeclaredVisibilityRules_EvaluateAcrossBooleanStates()
    {
        var ruleCount = 0;
        foreach (var descriptor in NodeFactoryRegistrar.Descriptors)
        {
            if (descriptor.Factory() is not PatchNodeViewModel node)
            {
                continue;
            }
            foreach (var port in node.Input)
            {
                if (port.IsVisibleWhen is not { } rule)
                {
                    continue;
                }
                ruleCount++;
                _ = rule(node);
                foreach (var input in node.Input.Where(candidate => candidate.PortType == NodePortType.Boolean))
                {
                    input.InitialValue = true;
                }
                _ = rule(node);
                foreach (var input in node.Input.Where(candidate => candidate.PortType == NodePortType.Boolean))
                {
                    input.InitialValue = false;
                }
            }
        }

        Assert.True(ruleCount >= 20);
    }
}