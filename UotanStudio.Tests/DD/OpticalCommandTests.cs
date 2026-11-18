using UotanStudio.DD.Drives.Windows;
using UotanStudio.DD.Writing;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class OpticalCommandTests
{
    [Fact]
    public void Xorriso_CloseSession_IncludesCloseAndCommit()
    {
        var args = OpticalBurnArguments.Xorriso("/dev/sr0", "/tmp/disc.iso", closeSession: true);

        Assert.Equal("-outdev", args[1]);
        Assert.Equal("/dev/sr0", args[2]);
        Assert.Contains("-map", args);
        Assert.Contains(args, arg => arg.Contains("/tmp/disc.iso", StringComparison.Ordinal));
        Assert.Contains("-close", args);
        Assert.Contains("on", args);
        Assert.Equal("-commit", args[^1]);
    }

    [Fact]
    public void Xorriso_LeaveSessionOpen_OmitsClose()
    {
        var args = OpticalBurnArguments.Xorriso("/dev/sr1", "/tmp/disc.iso", closeSession: false);

        Assert.DoesNotContain("-close", args);
        Assert.Equal("-commit", args[^1]);
    }

    [Fact]
    public void DrutilBurn_UsesDeviceIndexAndImage()
    {
        var args = OpticalBurnArguments.DrutilBurn(2, "/tmp/disc.iso", closeSession: true);

        Assert.Equal("burn", args[0]);
        Assert.Equal("-device", args[1]);
        Assert.Equal("2", args[2]);
        Assert.Equal("/tmp/disc.iso", args[^1]);
    }

    [Fact]
    public void WindowsListScript_UsesImapiDiscMasterAndTabSeparator()
    {
        var script = WindowsOpticalCommand.BuildListScript();

        Assert.Contains("IMAPI2.MsftDiscMaster2", script, StringComparison.Ordinal);
        Assert.Contains("IMAPI2.MsftDiscRecorder2", script, StringComparison.Ordinal);
        Assert.Contains("RECORDER", script, StringComparison.Ordinal);
        Assert.Contains("[char]9", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBurnScript_UsesImapiRawWriterAndQuotesImagePath()
    {
        var script = WindowsOpticalCommand.BuildBurnScript(
            "id-with-'quote'",
            "C:\\images\\disc.iso",
            closeSession: true);

        Assert.Contains("IMAPI2.MsftDiscFormat2RawCD", script, StringComparison.Ordinal);
        Assert.Contains("WriteMedia", script, StringComparison.Ordinal);
        Assert.Contains("PrepareMedia", script, StringComparison.Ordinal);
        Assert.Contains("ReleaseMedia", script, StringComparison.Ordinal);
        Assert.Contains("id-with-''quote''", script, StringComparison.Ordinal);
        Assert.Contains("C:\\images\\disc.iso", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBurnScript_LeaveSessionOpen_OmitsReleaseMedia()
    {
        var script = WindowsOpticalCommand.BuildBurnScript("id", "/tmp/disc.iso", closeSession: false);

        Assert.DoesNotContain("ReleaseMedia", script, StringComparison.Ordinal);
    }
}