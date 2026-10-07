using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class UsbTopologyTreeTests
{
    [Fact]
    public void Build_ChainedTopology_NestsEveryLevelUnderItsParent()
    {
        var nodes = new List<UsbTopologyNode>
        {
            Node("pci0", kind: UsbTopologyNodeKind.HostController),
            Node("root1", "pci0", UsbTopologyNodeKind.RootHub),
            Node("dev1", "root1"),
            Node("dev1:1.0", "dev1", UsbTopologyNodeKind.Function, interfaceNumber: 0)
        };

        var roots = UsbTopologyTree.Build(nodes);

        var controller = Assert.Single(roots);
        Assert.Equal("pci0", controller.Node.InstanceId);
        var rootHub = Assert.Single(controller.Children);
        Assert.Equal("root1", rootHub.Node.InstanceId);
        var device = Assert.Single(rootHub.Children);
        Assert.Equal("dev1", device.Node.InstanceId);
        var function = Assert.Single(device.Children);
        Assert.Equal("dev1:1.0", function.Node.InstanceId);
        Assert.Equal(4, UsbTopologyTree.Flatten(roots).Count);
    }

    [Fact]
    public void Build_ParentOutsideNodeSet_KeepsNodeReachableAsRoot()
    {
        var nodes = new List<UsbTopologyNode> { Node("dev1", "unseen-controller") };

        var roots = UsbTopologyTree.Build(nodes);

        Assert.Equal("dev1", Assert.Single(roots).Node.InstanceId);
        Assert.Single(UsbTopologyTree.Flatten(roots));
    }

    [Fact]
    public void Build_SelfParentingNode_BecomesRootWithoutDroppingIt()
    {
        var nodes = new List<UsbTopologyNode> { Node("dev1", "dev1") };

        var roots = UsbTopologyTree.Build(nodes);

        Assert.Equal("dev1", Assert.Single(roots).Node.InstanceId);
        Assert.Empty(roots[0].Children);
    }

    [Fact]
    public void Build_TwoNodeCycle_KeepsEveryNodeExactlyOnce()
    {
        List<UsbTopologyNode> nodes = [Node("cycle-a", "cycle-b"), Node("cycle-b", "cycle-a")];

        var roots = UsbTopologyTree.Build(nodes);

        Assert.Equal(2, roots.Count);
        Assert.All(roots, root => Assert.Empty(root.Children));
        Assert.Equal(2, UsbTopologyTree.Flatten(roots).Count);
    }

    [Fact]
    public void Build_MixedSiblings_OrdersByKindThenInterfaceNumber()
    {
        var nodes = new List<UsbTopologyNode>
        {
            Node("dev1", kind: UsbTopologyNodeKind.Device),
            Node("func2", "dev1", UsbTopologyNodeKind.Function, interfaceNumber: 2),
            Node("func1", "dev1", UsbTopologyNodeKind.Function, interfaceNumber: 1),
            Node("hub1", "dev1", UsbTopologyNodeKind.Hub)
        };

        var roots = UsbTopologyTree.Build(nodes);

        var device = Assert.Single(roots);
        Assert.Equal(3, device.Children.Count);
        Assert.Equal("hub1", device.Children[0].Node.InstanceId);
        Assert.Equal("func1", device.Children[1].Node.InstanceId);
        Assert.Equal("func2", device.Children[2].Node.InstanceId);
    }

    [Fact]
    public void Retain_ProtocolMatch_KeepsAncestorChainAndDropsUnrelatedBranch()
    {
        var nodes = new List<UsbTopologyNode>
        {
            Node("root-a", kind: UsbTopologyNodeKind.RootHub),
            Node("phone", "root-a"),
            Node("phone-adb", "phone", UsbTopologyNodeKind.Function, protocol: "adb"),
            Node("root-b", kind: UsbTopologyNodeKind.RootHub),
            Node("keyboard", "root-b")
        };
        var roots = UsbTopologyTree.Build(nodes);

        var filtered = UsbTopologyTree.Retain(roots, node => node.Protocol is not null);

        var keptRoot = Assert.Single(filtered);
        Assert.Equal("root-a", keptRoot.Node.InstanceId);
        var phone = Assert.Single(keptRoot.Children);
        var adb = Assert.Single(phone.Children);
        Assert.Equal("phone-adb", adb.Node.InstanceId);
    }

    [Fact]
    public void Build_DuplicateInstanceIds_KeepsFirstOccurrenceOnly()
    {
        List<UsbTopologyNode> nodes = [Node("dev1", description: "first"), Node("dev1", description: "second")];

        var roots = UsbTopologyTree.Build(nodes);

        Assert.Equal("first", Assert.Single(roots).Node.Description);
    }

    [Fact]
    public void Build_BlankInstanceId_ExcludesOnlyTheUnusableNode()
    {
        List<UsbTopologyNode> nodes = [Node(string.Empty), Node("dev1")];

        var roots = UsbTopologyTree.Build(nodes);

        Assert.Equal("dev1", Assert.Single(roots).Node.InstanceId);
    }

    [Fact]
    public void Build_NoNodes_ReturnsNoRoots()
    {
        Assert.Empty(UsbTopologyTree.Build([]));
    }

    private static UsbTopologyNode Node(string instanceId, string? parent = null,
        UsbTopologyNodeKind kind = UsbTopologyNodeKind.Device, byte? interfaceNumber = null,
        string? protocol = null, string? description = null) => new()
        {
            InstanceId = instanceId,
            ParentInstanceId = parent,
            Kind = kind,
            InterfaceNumber = interfaceNumber,
            Protocol = protocol,
            Description = description
        };
}