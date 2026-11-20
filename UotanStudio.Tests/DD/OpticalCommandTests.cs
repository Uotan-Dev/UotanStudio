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
    public void Imapi2InteropSlots_MatchSdkHeaderVtableOrder()
    {
        // Frozen against the Windows SDK imapi2.h / ocidl.h C++ declarations; a mismatch silently
        // calls the wrong native method, so any SDK change must bump these values.
        Assert.Equal(8, Imapi2Interop.MasGetItem);
        Assert.Equal(9, Imapi2Interop.MasGetCount);
        Assert.Equal(10, Imapi2Interop.MasGetIsSupportedEnvironment);
        Assert.Equal(13, Imapi2Interop.RecInitializeDiscRecorder);
        Assert.Equal(15, Imapi2Interop.RecGetVendorId);
        Assert.Equal(16, Imapi2Interop.RecGetProductId);
        Assert.Equal(7, Imapi2Interop.FmtPutRecorder);
        Assert.Equal(13, Imapi2Interop.FmtGetCurrentMediaStatus);
        Assert.Equal(20, Imapi2Interop.FmtPutForceMediaToBeClosed);
        Assert.Equal(24, Imapi2Interop.FmtGetCurrentPhysicalMediaType);
        Assert.Equal(25, Imapi2Interop.FmtPutClientName);
        Assert.Equal(36, Imapi2Interop.FmtWrite);
        Assert.Equal(37, Imapi2Interop.FmtCancelWrite);
        Assert.Equal(0x200, Imapi2Interop.UpdateDispatchId);
        Assert.Equal(0x4, Imapi2Interop.WriteActionWritingData);
        Assert.Equal(0x7, Imapi2Interop.WriteActionVerifying);
        Assert.Equal(0x1, Imapi2Interop.MediaStateOverwriteOnly);
        Assert.Equal(0x2, Imapi2Interop.MediaStateBlank);
        Assert.Equal(0x4, Imapi2Interop.MediaStateAppendable);
        Assert.Equal(0xFC00, Imapi2Interop.MediaStateUnsupportedMask);
        Assert.Equal(0x2000, Imapi2Interop.MediaStateWriteProtected);
        Assert.Equal(0x4000, Imapi2Interop.MediaStateFinalized);
        Assert.Equal(0x8000, Imapi2Interop.MediaStateUnsupportedMedia);
    }

    [Fact]
    public void WindowsMediaTypeToFormat_MapsImapiPhysicalTypes()
    {
        Assert.Equal(OpticalDiscFormat.Cd, WindowsOpticalDriveScanner.MediaTypeToFormat(1));
        Assert.Equal(OpticalDiscFormat.Cd, WindowsOpticalDriveScanner.MediaTypeToFormat(3));
        Assert.Equal(OpticalDiscFormat.Dvd, WindowsOpticalDriveScanner.MediaTypeToFormat(4));
        Assert.Equal(OpticalDiscFormat.Dvd, WindowsOpticalDriveScanner.MediaTypeToFormat(0xD));
        Assert.Equal(OpticalDiscFormat.BluRay, WindowsOpticalDriveScanner.MediaTypeToFormat(0x11));
        Assert.Equal(OpticalDiscFormat.BluRay, WindowsOpticalDriveScanner.MediaTypeToFormat(0x13));
        Assert.Equal(OpticalDiscFormat.Unknown, WindowsOpticalDriveScanner.MediaTypeToFormat(0));
        Assert.Equal(OpticalDiscFormat.Unknown, WindowsOpticalDriveScanner.MediaTypeToFormat(-1));
        Assert.Equal(OpticalDiscFormat.Unknown, WindowsOpticalDriveScanner.MediaTypeToFormat(0x14));
    }

    [Fact]
    public void WindowsIsWritableMedia_AcceptsRecordableFamiliesOnly()
    {
        Assert.False(WindowsOpticalDriveScanner.IsWritableMedia(-1));
        Assert.False(WindowsOpticalDriveScanner.IsWritableMedia(0));
        Assert.False(WindowsOpticalDriveScanner.IsWritableMedia(1));
        Assert.True(WindowsOpticalDriveScanner.IsWritableMedia(2));
        Assert.True(WindowsOpticalDriveScanner.IsWritableMedia(3));
        Assert.True(WindowsOpticalDriveScanner.IsWritableMedia(5));
        Assert.True(WindowsOpticalDriveScanner.IsWritableMedia(0xD));
        Assert.True(WindowsOpticalDriveScanner.IsWritableMedia(0x12));
        Assert.True(WindowsOpticalDriveScanner.IsWritableMedia(0x13));
    }

    [Fact]
    public void WindowsIsWritableState_RejectsErrorBitsAndAcceptsWritableBits()
    {
        Assert.False(WindowsOpticalDriveScanner.IsWritableState(-1));
        Assert.True(WindowsOpticalDriveScanner.IsWritableState(0x2));
        Assert.True(WindowsOpticalDriveScanner.IsWritableState(0x4));
        Assert.True(WindowsOpticalDriveScanner.IsWritableState(0x1));
        Assert.True(WindowsOpticalDriveScanner.IsWritableState(0x2 | 0x4));
        Assert.False(WindowsOpticalDriveScanner.IsWritableState(0x400));
        Assert.False(WindowsOpticalDriveScanner.IsWritableState(0x800));
        Assert.False(WindowsOpticalDriveScanner.IsWritableState(0x2000));
        Assert.False(WindowsOpticalDriveScanner.IsWritableState(0x4000));
        Assert.False(WindowsOpticalDriveScanner.IsWritableState(0x8000));
        Assert.False(WindowsOpticalDriveScanner.IsWritableState(0x2 | 0x2000));
    }
}