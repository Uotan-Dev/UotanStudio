using UotanStudio.Firmware.BoundedContexts.Patching;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchApplicabilityTests
{
    [Theory]
    [InlineData(false, false, false, PatchApplicability.NotApplicable)]
    [InlineData(false, false, true, PatchApplicability.NotApplicable)]
    [InlineData(false, true, false, PatchApplicability.NotApplicable)]
    [InlineData(false, true, true, PatchApplicability.NotApplicable)]
    [InlineData(true, true, false, PatchApplicability.Applicable)]
    [InlineData(true, false, true, PatchApplicability.AlreadyApplied)]
    [InlineData(true, false, false, PatchApplicability.Conflict)]
    [InlineData(true, true, true, PatchApplicability.Conflict)]
    public void Evaluate_StateCombination_ClassifiesWithoutAssumingPublication(bool compatible, bool original, bool desired,
        PatchApplicability expected)
        => Assert.Equal(expected, PatchApplicabilityCheck.Evaluate(compatible, original, desired));

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, false)]
    public async Task Execute_ApplicabilityBranch_OnlyPublishesApplicableTarget(bool compatible, bool original, bool desired, bool publishes)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var target = Path.Combine(temp.Path, "output");
        var check = PatchNodeViewModel.Create("Validation.Applicability.Check");
        Set(check, "Compatible", compatible);
        Set(check, "Original State Matches", original);
        Set(check, "Desired State Matches", desired);
        var branch = FlowControlNodeViewModel.CreateBranchNode();
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        Set(write, "Output Path", target);
        Set(write, "Text", "patched");

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([check, branch, write],
            [Connect(check, "Should Apply", branch, "Condition"), Connect(branch, "True", write, "Flow")], token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(publishes, File.Exists(target));
        Assert.Equal(publishes ? NodeExecutionStatus.Succeeded : NodeExecutionStatus.Skipped, write.ExecutionStatus);
        Assert.Equal(publishes ? PatchRunOutcome.Applied : compatible ? PatchRunOutcome.AlreadyApplied : PatchRunOutcome.NotApplicable,
            result.PatchOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_AmbiguousState_DiscardsEarlierStagedWrite(bool bothMatch)
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "output");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        Set(write, "Output Path", target);
        Set(write, "Text", "must not publish");
        var check = PatchNodeViewModel.Create("Validation.Applicability.Check");
        Set(check, "Compatible", true);
        Set(check, "Original State Matches", bothMatch);
        Set(check, "Desired State Matches", bothMatch);

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([write, check],
            [Connect(write, "Completed", check, "Flow")], TestContext.Current.CancellationToken);

        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(PatchRunOutcome.Conflict, result.PatchOutcome);
        Assert.False(File.Exists(target));
        Assert.Equal("Conflict", check.Output.Single(port => port.Title == "Applicability").Data);
    }

    [Fact]
    public async Task Execute_ApplicableWithoutMutation_ReportsNoChangesRatherThanApplied()
    {
        var check = PatchNodeViewModel.Create("Validation.Applicability.Check");
        Set(check, "Compatible", true);
        Set(check, "Original State Matches", true);

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([check], [], TestContext.Current.CancellationToken);

        Assert.Equal(PatchRunOutcome.NoChanges, result.PatchOutcome);
    }

    private static void Set(FlowNodeViewModel node, string title, object value)
        => node.Input.Single(port => port.Title == title).InitialValue = value;

    private static NodeConnection Connect(FlowNodeViewModel source, string output, FlowNodeViewModel target, string input) => new()
    {
        SourceInstanceId = source.InstanceId,
        SourceConnector = output,
        TargetInstanceId = target.InstanceId,
        TargetConnector = input
    };

    private sealed class Log : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}