using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.MacOS;
using UotanStudio.DD.Drives.Windows;
using UotanStudio.DD.Writing;
using Xunit;

namespace UotanStudio.Tests.DD;

public sealed class OpticalCommandTests
{
    [Fact]
    public void Xorriso_UsesCdrecordPersonalityAndDevice()
    {
        var args = OpticalBurnArguments.Xorriso("/dev/sr0", "/tmp/disc.iso", closeSession: true);

        Assert.Equal("-as", args[0]);
        Assert.Equal("cdrecord", args[1]);
        Assert.Contains("dev=/dev/sr0", args);
        Assert.Contains("blank=as_needed", args);
        Assert.Equal("/tmp/disc.iso", args[^1]);
        Assert.DoesNotContain("-nodisk_at_status", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("-map", StringComparison.Ordinal));
        Assert.DoesNotContain(args, arg => arg.StartsWith("-outdev", StringComparison.Ordinal));
    }

    [Fact]
    public void Xorriso_LeaveSessionOpen_AddsMulti()
    {
        var args = OpticalBurnArguments.Xorriso("/dev/sr1", "/tmp/disc.iso", closeSession: false);

        Assert.Contains("-multi", args);
        Assert.Equal("/tmp/disc.iso", args[^1]);
    }

    [Fact]
    public void Xorriso_CloseSession_OmitsMulti()
    {
        var args = OpticalBurnArguments.Xorriso("/dev/sr1", "/tmp/disc.iso", closeSession: true);

        Assert.DoesNotContain("-multi", args);
    }

    [Fact]
    public void DrutilBurn_UsesDriveOptionBeforeVerbAndPassesIndexUnchanged()
    {
        var args = OpticalBurnArguments.DrutilBurn(2, "/tmp/disc.iso", closeSession: true);

        Assert.Equal("-drive", args[0]);
        Assert.Equal("2", args[1]);
        Assert.Equal("burn", args[2]);
        Assert.Equal("/tmp/disc.iso", args[^1]);
        Assert.DoesNotContain("-device", args);
        Assert.DoesNotContain("-noverify", args);
    }

    [Fact]
    public void DrutilBurn_FirstRecorder_MapsToListedOrdinalOne()
    {
        var args = OpticalBurnArguments.DrutilBurn(1, "/tmp/disc.iso", closeSession: true);

        Assert.Equal("-drive", args[0]);
        Assert.Equal("1", args[1]);
        Assert.Equal("burn", args[2]);
    }

    [Fact]
    public void DrutilBurn_NonPositiveIndex_ThrowsInsteadOfRecordingToAnotherDrive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpticalBurnArguments.DrutilBurn(0, "/tmp/disc.iso", closeSession: true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpticalBurnArguments.DrutilBurn(-1, "/tmp/disc.iso", closeSession: false));
    }

    [Fact]
    public void DrutilBurn_LeaveSessionOpen_UsesAppendable()
    {
        var args = OpticalBurnArguments.DrutilBurn(1, "/tmp/disc.iso", closeSession: false);

        Assert.Contains("-appendable", args);
        Assert.Equal("/tmp/disc.iso", args[^1]);
    }

    [Fact]
    public void DrutilBurn_CloseSession_OmitsAppendable()
    {
        var args = OpticalBurnArguments.DrutilBurn(1, "/tmp/disc.iso", closeSession: true);

        Assert.DoesNotContain("-appendable", args);
    }

    [Fact]
    public void MacOpticalDevice_ParsesRealXmlListingWithDoctypeProlog()
    {
        var devices = MacOpticalDevice.ParseList(MacOpticalDeviceFixtures.RealListXml);

        var device = Assert.Single(devices);
        Assert.Equal(1, device.Index);
        Assert.Equal("drutil:1", device.Name);
        Assert.Equal("ASUS SBW-06D2X-U", device.Description);
    }

    [Fact]
    public void MacOpticalDevice_MultipleRecorders_KeepsListedOrdinals()
    {
        const string xml = "<deviceList>" +
            "<device name=\"HL-DT-ST BD-RE BH16NS40\" index=\"1\"><vendor name=\"HL-DT-ST\"/>" +
            "<product name=\"BD-RE BH16NS40\"/></device>" +
            "<device name=\"ASUS BW-16D1HT\" index=\"2\"><vendor name=\"ASUS\"/>" +
            "<product name=\"BW-16D1HT\"/></device>" +
            "</deviceList>";

        var devices = MacOpticalDevice.ParseList(xml);

        Assert.Equal(2, devices.Count);
        Assert.Equal(1, devices[0].Index);
        Assert.Equal("drutil:1", devices[0].Name);
        Assert.Equal("HL-DT-ST BD-RE BH16NS40", devices[0].Description);
        Assert.Equal(2, devices[1].Index);
        Assert.Equal("drutil:2", devices[1].Name);
    }

    [Fact]
    public void MacOpticalDevice_ZeroBasedIndex_IsRejectedRatherThanRebased()
    {
        const string xml = "<deviceList><device index=\"0\"><vendor name=\"ASUS\"/>" +
            "<product name=\"BW-16D1HT\"/></device></deviceList>";

        Assert.Empty(MacOpticalDevice.ParseList(xml));
    }

    [Fact]
    public void MacOpticalDevice_EmptyListing_ReturnsNothing()
    {
        var devices = MacOpticalDevice.ParseList("<deviceList/>");

        Assert.Empty(devices);
    }

    [Fact]
    public void MacOpticalDevice_TruncatedXml_ReturnsNothingInsteadOfThrowing()
    {
        Assert.Empty(MacOpticalDevice.ParseList(MacOpticalDeviceFixtures.RealListXml[..200]));
        Assert.Empty(MacOpticalDevice.ParseList(string.Empty));
    }

    [Fact]
    public void MacOpticalDevice_NoMedia_ReportsUnknownAndNotWritable()
    {
        var (format, writable) = MacOpticalDevice.ParseStatus(MacOpticalDeviceFixtures.NoMediaStatusXml);

        Assert.Equal(OpticalDiscFormat.Unknown, format);
        Assert.False(writable);
    }

    [Fact]
    public void MacOpticalDevice_DvdMediaInfo_ReportsDvdAndWritable()
    {
        var (format, writable) = MacOpticalDevice.ParseStatus(MacOpticalDeviceFixtures.DvdStatusXml);

        Assert.Equal(OpticalDiscFormat.Dvd, format);
        Assert.True(writable);
    }

    [Fact]
    public void MacOpticalDevice_BluRayMediaInfo_ReportsBluRayAndWritable()
    {
        var (format, writable) = MacOpticalDevice.ParseStatus(MacOpticalDeviceFixtures.BluRayStatusXml);

        Assert.Equal(OpticalDiscFormat.BluRay, format);
        Assert.True(writable);
    }

    [Fact]
    public void MacOpticalDevice_CdMediaClass_ReportsCd()
    {
        var (format, writable) = MacOpticalDevice.ParseStatus(MacOpticalDeviceFixtures.CdStatusXml);

        Assert.Equal(OpticalDiscFormat.Cd, format);
        Assert.True(writable);
    }

    [Fact]
    public void MacOpticalDevice_AppendableSession_IsWritableWithoutBlankMedia()
    {
        var (_, writable) = MacOpticalDevice.ParseStatus(MacOpticalDeviceFixtures.AppendableStatusXml);

        Assert.True(writable);
    }

    [Fact]
    public void MacOpticalDevice_BusyOrTransitioningMedia_IsNotWritable()
    {
        Assert.False(MacOpticalDevice.ParseStatus(MacOpticalDeviceFixtures.BusyStatusXml).Writable);
        Assert.False(MacOpticalDevice.ParseStatus(MacOpticalDeviceFixtures.TransitionStatusXml).Writable);
    }

    [Fact]
    public void MacOpticalDevice_MissingStatusDocument_IsNotWritable()
    {
        Assert.False(MacOpticalDevice.ParseStatus("<statusdoc/>").Writable);
        Assert.False(MacOpticalDevice.ParseStatus("<deviceList/>").Writable);
    }

    [Fact]
    public void MacOpticalDriveScanner_StatusArguments_SelectListedOrdinal()
    {
        var args = MacOpticalDevice.StatusArguments(1);

        Assert.Equal(new[] { "-drive", "1", "status", "-xml" }, args);
        Assert.Equal(new[] { "-drive", "2", "status", "-xml" }, MacOpticalDevice.StatusArguments(2));
    }

    [Fact]
    public void WindowsListScript_UsesImapiDiscMasterAndMediaState()
    {
        var script = WindowsOpticalCommand.BuildListScript();

        Assert.Contains("IMAPI2.MsftDiscMaster2", script, StringComparison.Ordinal);
        Assert.Contains("IMAPI2.MsftDiscRecorder2", script, StringComparison.Ordinal);
        Assert.Contains("IMAPI2.MsftDiscFormat2Data", script, StringComparison.Ordinal);
        Assert.Contains("CurrentPhysicalMediaType", script, StringComparison.Ordinal);
        Assert.Contains("CurrentMediaStatus", script, StringComparison.Ordinal);
        Assert.Contains("[char]9", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBurnScript_UsesDataWriterAndQuotesImagePath()
    {
        var script = WindowsOpticalCommand.BuildBurnScript(
            "id-with-'quote'",
            "C:\\images\\disc.iso",
            closeSession: true);

        Assert.Contains("IMAPI2.MsftDiscFormat2Data", script, StringComparison.Ordinal);
        Assert.DoesNotContain("MsftDiscFormat2RawCD", script, StringComparison.Ordinal);
        Assert.Contains("$writer.Write($stream)", script, StringComparison.Ordinal);
        Assert.Contains("ForceMediaToBeClosed = $true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareMedia", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseMedia", script, StringComparison.Ordinal);
        Assert.Contains("id-with-''quote''", script, StringComparison.Ordinal);
        Assert.Contains("C:\\images\\disc.iso", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBurnScript_LeaveSessionOpen_ClearsForceMediaToBeClosed()
    {
        var script = WindowsOpticalCommand.BuildBurnScript("id", "/tmp/disc.iso", closeSession: false);

        Assert.Contains("ForceMediaToBeClosed = $false", script, StringComparison.Ordinal);
    }
}