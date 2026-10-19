using UotanStudio.Firmware.Patching;
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