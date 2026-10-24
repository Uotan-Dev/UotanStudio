using UotanStudio.Views.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class ScreenMirrorCompositionRecoveryTests
{
    [Fact]
    public void BeginAttach_ThenBeginDetach_InvalidatesInFlightProbeGeneration()
    {
        var recovery = new ScreenMirrorCompositionRecovery();
        var generation = recovery.BeginAttach();
        Assert.True(recovery.IsCurrent(generation));

        recovery.BeginDetach();

        Assert.False(recovery.IsCurrent(generation));
        Assert.False(recovery.IsCurrent(0));
    }

    [Fact]
    public void RecordFailure_Recoverable_RebuildsSurfaceOnNextAttach()
    {
        var recovery = new ScreenMirrorCompositionRecovery();
        _ = recovery.BeginAttach();

        Assert.True(recovery.RecordFailure(terminal: false));
        Assert.True(recovery.Failed);
        Assert.False(recovery.IsTerminal);

        var generation = recovery.BeginAttach();

        Assert.NotEqual(0, generation);
        Assert.False(recovery.Failed);
        Assert.True(recovery.IsCurrent(generation));
    }

    [Fact]
    public void RecordFailure_Terminal_RefusesEveryLaterAttach()
    {
        var recovery = new ScreenMirrorCompositionRecovery();
        _ = recovery.BeginAttach();

        Assert.True(recovery.RecordFailure(terminal: true));
        Assert.True(recovery.IsTerminal);
        Assert.True(recovery.Failed);

        Assert.Equal(0, recovery.BeginAttach());
        Assert.Equal(0, recovery.BeginAttach());
    }

    [Fact]
    public void RecordFailure_OnlyTheFirstFailureOfAnEpisodeIsReported()
    {
        var recovery = new ScreenMirrorCompositionRecovery();
        _ = recovery.BeginAttach();

        Assert.True(recovery.RecordFailure(terminal: false));
        Assert.False(recovery.RecordFailure(terminal: false));
        Assert.False(recovery.RecordFailure(terminal: true));
        Assert.True(recovery.IsTerminal);
    }

    [Fact]
    public void RecordAttached_AfterRecoverableFailure_MakesTheNextFailureAnEpisodeStart()
    {
        var recovery = new ScreenMirrorCompositionRecovery();
        var first = recovery.BeginAttach();
        Assert.True(recovery.RecordFailure(terminal: false));
        Assert.True(recovery.IsCurrent(first));

        var second = recovery.BeginAttach();
        recovery.RecordAttached();
        Assert.False(recovery.Failed);
        Assert.True(recovery.IsCurrent(second));

        Assert.True(recovery.RecordFailure(terminal: false));
    }
}