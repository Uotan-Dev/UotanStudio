using UotanStudio.Services.Patching;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Capability policy: read-only nodes must not force patch-safe evaluation of a graph.<para>能力策略：只读节点不得使整张图被判定为需要补丁安全评估。</para></summary>
[Collection("Node editor UI")]
public sealed class PatchWorkflowPolicyTests
{
    [Theory]
    [InlineData("Flow.Applicability.Gate")]
    [InlineData("File.Text.Read")]
    [InlineData("Resource.Asset.Read")]
    [InlineData("Workspace.Directory.Open")]
    public void Descriptor_ReadOnlyPatchNode_DeclaresPatchSafeWithoutPatchOperation(string typeKey)
    {
        NodeFactoryRegistrar.RegisterAll();

        var descriptor = Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == typeKey);

        Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.PatchSafe));
        Assert.False(descriptor.Capabilities.HasFlag(NodeCapability.PatchOperation));
    }

    [Theory]
    [InlineData("Device.Target.Select")]
    [InlineData("Device.Property.Read")]
    [InlineData("Device.Table.Read")]
    [InlineData("Device.Partition.Read")]
    [InlineData("Device.Slot.Query")]
    public void Descriptor_ReadOnlyDeviceNode_DeclaresPatchSafe(string typeKey)
    {
        NodeFactoryRegistrar.RegisterAll();

        var descriptor = Assert.Single(NodeFactoryRegistrar.Descriptors, item => item.TypeKey == typeKey);

        Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.PatchSafe));
        Assert.False(descriptor.Capabilities.HasFlag(NodeCapability.PatchOperation));
        Assert.False(descriptor.RequiresConfirmation);
    }

    [Theory]
    [InlineData("Flow.Applicability.Gate")]
    [InlineData("Resource.Asset.Read")]
    public void Inspect_ReadOnlyNodeWithDeviceFlash_DoesNotReportPatchOperations(string readOnlyTypeKey)
    {
        NodeFactoryRegistrar.RegisterAll();

        var policy = PatchWorkflowPolicy.Inspect([Node(readOnlyTypeKey), Node("Device.Partition.Flash")]);

        Assert.False(policy.HasPatches);
    }

    [Fact]
    public void Inspect_WritablePatchNodeWithDeviceFlash_StillReportsPatchOperations()
    {
        NodeFactoryRegistrar.RegisterAll();

        var policy = PatchWorkflowPolicy.Inspect([Node("Image.Text.Edit"), Node("Device.Partition.Flash")]);

        Assert.True(policy.HasPatches);
        Assert.False(policy.Safe);
    }

    private static FlowNodeViewModel Node(string typeKey)
        => NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();
}