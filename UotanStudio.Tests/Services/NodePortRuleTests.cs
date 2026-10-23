using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NodePortRuleTests
{
    [Fact]
    public async Task IsSatisfiedBy_AllComparisonKinds_AreCaseSensitiveAndExplicit()
    {
        await Task.CompletedTask;
        var satisfied = new SatisfiedNode();
        var unsatisfied = new UnsatisfiedNode();

        NodePortRule[] rules =
        [
            NodePortRule.IsOn("Toggle"),
            new("Toggle", NodePortComparison.IsOff),
            new("Mode", NodePortComparison.Equals, "Replace"),
            new("Mode", NodePortComparison.Equals, "Delete"),
            NodePortRule.NotEquals("Mode", "Delete"),
            NodePortRule.MayEqualAny("Mode", "Replace", "Insert")
        ];

        Assert.Equal(
            [true, false, true, false, true, true],
            rules.Select(rule => rule.IsSatisfiedBy(satisfied)));
        Assert.Equal(
            [false, true, false, true, false, false],
            rules.Select(rule => rule.IsSatisfiedBy(unsatisfied)));
    }

    [Fact]
    public async Task IsSatisfiedBy_ConnectedSource_KeepsOnCompatibleRulesVisible()
    {
        await Task.CompletedTask;
        var node = new ConnectedNode();

        Assert.True(NodePortRule.IsOn("Toggle").IsSatisfiedBy(node));
        Assert.False(new NodePortRule("Toggle", NodePortComparison.IsOff).IsSatisfiedBy(node));
        Assert.False(new NodePortRule("Mode", NodePortComparison.Equals, "Replace").IsSatisfiedBy(node));
        Assert.False(NodePortRule.NotEquals("Mode", "Delete").IsSatisfiedBy(node));
        Assert.True(NodePortRule.MayEqualAny("Mode", "Replace").IsSatisfiedBy(node));
    }

    [Fact]
    public async Task IsSatisfiedBy_BooleanPortValues_FollowLenientConversion()
    {
        await Task.CompletedTask;
        var node = new SatisfiedNode();
        var toggle = node.Input.Single(port => port.Title == "Toggle");
        var rule = NodePortRule.IsOn("Toggle");

        var observed = new List<bool>();
        foreach (var value in new object?[] { false, true, "false", "true", "0", "1", "yes", null })
        {
            toggle.InitialValue = value;
            observed.Add(rule.IsSatisfiedBy(node));
        }

        Assert.Equal([false, true, false, true, false, true, false, false], observed);
    }

    [Fact]
    public async Task IsSatisfiedBy_MissingPort_HidesGuardedPort()
    {
        await Task.CompletedTask;
        var node = new SatisfiedNode();

        Assert.False(new NodePortRule("Absent", NodePortComparison.MayEqualAny, "anything").IsSatisfiedBy(node));
        Assert.False(NodePortRule.IsOn("absent").IsSatisfiedBy(node));
    }

    [Fact]
    public async Task IsSatisfiedBy_NullNode_ThrowsArgumentNullException()
    {
        await Task.CompletedTask;

        Assert.Throws<ArgumentNullException>(() => NodePortRule.IsOn("Mode").IsSatisfiedBy(null!));
    }

    [Fact]
    public async Task IsSatisfiedBy_NumberAndBooleanPorts_CompareConvertedText()
    {
        await Task.CompletedTask;
        var node = new TypedNode();

        Assert.True(NodePortRule.IsOn("Flag").IsSatisfiedBy(node));
        Assert.True(new NodePortRule("Count", NodePortComparison.Equals, "3").IsSatisfiedBy(node));
        Assert.False(new NodePortRule("Count", NodePortComparison.Equals, "3.0").IsSatisfiedBy(node));
        Assert.True(new NodePortRule("Ratio", NodePortComparison.Equals, "1.5").IsSatisfiedBy(node));
    }

    [Fact]
    public async Task IsSatisfiedBy_ComparisonOutsideTheEvaluationTable_DefaultsToVisible()
    {
        await Task.CompletedTask;
        var node = new SatisfiedNode();

        Assert.True(new NodePortRule("Mode", (NodePortComparison)99).IsSatisfiedBy(node));
    }

    [Fact]
    public async Task MatchesAny_EmptyValuesOnlyMatchAbsentInitialValue()
    {
        await Task.CompletedTask;
        var node = new SatisfiedNode();

        Assert.False(new NodePortRule("Toggle", NodePortComparison.Equals).IsSatisfiedBy(node));
        Assert.True(new NodePortRule("Toggle", NodePortComparison.NotEquals).IsSatisfiedBy(node));
        Assert.False(new NodePortRule("Blank", NodePortComparison.Equals).IsSatisfiedBy(node));
        Assert.True(new NodePortRule("Blank", NodePortComparison.NotEquals).IsSatisfiedBy(node));
        Assert.False(new NodePortRule("Absent", NodePortComparison.NotEquals).IsSatisfiedBy(node));
    }

    [Fact]
    public void IsOn_KeepingPortDisconnected_ReevaluatesAfterValueChanges()
    {
        var node = new SatisfiedNode();
        var rule = NodePortRule.IsOn("Toggle");
        var toggle = node.Input.Single(port => port.Title == "Toggle");

        Assert.True(rule.IsSatisfiedBy(node));
        toggle.InitialValue = false;
        Assert.False(rule.IsSatisfiedBy(node));
        toggle.InitialValue = "0";
        Assert.False(rule.IsSatisfiedBy(node));
        toggle.InitialValue = "1";
        Assert.True(rule.IsSatisfiedBy(node));
        toggle.InitialValue = "yes";
        Assert.False(rule.IsSatisfiedBy(node));
        toggle.InitialValue = "true";
        Assert.True(rule.IsSatisfiedBy(node));
        toggle.InitialValue = "false";
        Assert.False(rule.IsSatisfiedBy(node));
    }

    [Fact]
    public void Rule_ReferenceAndRecordEquality_CompareValuesAsData()
    {
        var same = new NodePortRule("Mode", NodePortComparison.Equals, "Replace", "Insert");
        var alsoSame = new NodePortRule("Mode", NodePortComparison.Equals, new[] { "Replace", "Insert" });
        var different = new NodePortRule("Mode", NodePortComparison.Equals, "Insert", "Replace");

        Assert.Equal(same, alsoSame);
        Assert.Equal(same.GetHashCode(), alsoSame.GetHashCode());
        Assert.NotEqual(same, different);
        Assert.Equal("Mode", same.PortTitle);
        Assert.Equal(NodePortComparison.Equals, same.Comparison);
        Assert.Equal(new[] { "Replace", "Insert" }, same.Values);
        Assert.Equal(same with { PortTitle = "Operation" }, new NodePortRule("Operation", NodePortComparison.Equals, "Replace", "Insert"));
    }

    [Fact]
    public void DeclarationFactories_BuildEquivalentRulesAndDependencies()
    {
        var isOn = NodePortRule.IsOn("Toggle");
        var notEquals = NodePortRule.NotEquals("Operation", "Delete");
        var mayEqualAny = NodePortRule.MayEqualAny("Operation", "Replace", "Insert");
        var declaration = new NodeInputDeclaration("Target", NodePortType.Text, Rule: mayEqualAny);
        var plain = new NodeInputDeclaration("Plain", NodePortType.Text);

        Assert.Equal(new NodePortRule("Toggle", NodePortComparison.IsOn), isOn);
        Assert.Empty(isOn.Values);
        Assert.Equal(new NodePortRule("Operation", NodePortComparison.NotEquals, "Delete"), notEquals);
        Assert.Equal(new NodePortRule("Operation", NodePortComparison.MayEqualAny, "Replace", "Insert"), mayEqualAny);
        Assert.Equal(new[] { "Operation" }, declaration.VisibilityDependencies);
        Assert.Empty(plain.VisibilityDependencies);
    }

    private abstract class ProbeNode : FlowNodeViewModel
    {
        protected ProbeNode() : base("probe", NodeCategory.Data)
        {
        }

        protected NodeConnectorViewModel Add(string title, object? initialValue, NodePortType portType = NodePortType.Text)
        {
            var port = new NodeConnectorViewModel { Title = title, PortType = portType, InitialValue = initialValue };
            Input.Add(port);
            return port;
        }
    }

    private sealed class SatisfiedNode : ProbeNode
    {
        public SatisfiedNode()
        {
            Add("Toggle", true, NodePortType.Boolean);
            Add("Mode", "Replace");
            Add("Blank", null);
        }
    }

    private sealed class UnsatisfiedNode : ProbeNode
    {
        public UnsatisfiedNode()
        {
            Add("Toggle", false, NodePortType.Boolean);
            Add("Mode", "Delete");
            Add("Blank", null);
        }
    }

    private sealed class ConnectedNode : ProbeNode
    {
        public ConnectedNode()
        {
            Add("Toggle", false, NodePortType.Boolean).SourceDescription = "Source → Toggle";
            Add("Mode", "Delete").SourceDescription = "Source → Mode";
        }
    }

    private sealed class TypedNode : ProbeNode
    {
        public TypedNode()
        {
            Add("Flag", "1", NodePortType.Boolean);
            Add("Count", 3, NodePortType.Number);
            Add("Ratio", 1.5, NodePortType.Number);
        }
    }
}