using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Rockchip loader reconnection evidence: the loader-stage replacement of a maskrom port matches by
/// unchanged physical location plus loader-stage evidence (<c>bcdUSB</c> bit 0 or the vendor's
/// <c>ROCKCHIP</c> serial), while the OS container id may change with the stage switch.
/// <para>瑞芯微 loader 重连证据：maskrom 端口的 loader 阶段替代端点按不变的物理位置加 loader 阶段
/// 证据（<c>bcdUSB</c> bit 0 或厂商 <c>ROCKCHIP</c> 序列号）匹配，而操作系统容器 ID 允许随阶段切换变化。</para>
/// </summary>
public sealed class DeviceControllerRockchipReconnectionTests
{
    private static UsbDeviceInfo MaskromPort() => new()
    {
        VendorId = 0x2207,
        ProductId = 0x350B,
        SerialNumber = "0000000000",
        DeviceInstanceId = @"USB\VID_2207&PID_350B\6&2F0A3B7&0&2",
        ContainerId = Guid.NewGuid(),
    };

    private static UsbDeviceInfo LoaderPort(string serial = "ROCKCHIP", Guid? containerId = null) => new()
    {
        VendorId = 0x2207,
        ProductId = 0x350B,
        SerialNumber = serial,
        DeviceInstanceId = @"USB\VID_2207&PID_350B\6&2F0A3B7&0&2",
        ContainerId = containerId ?? Guid.NewGuid(),
    };

    [Fact]
    public void MatchesRockchipLoaderEvidence_RockchipSerialOnSameInstanceTail_MatchesAcrossContainerChange()
    {
        var previous = MaskromPort();
        var replacement = LoaderPort(containerId: Guid.NewGuid());

        Assert.True(DeviceController.MatchesRockchipLoaderEvidence(previous, replacement));
    }

    [Fact]
    public void MatchesRockchipLoaderEvidence_SamePortOnOtherHubInstance_IsRefused()
    {
        var previous = MaskromPort();
        var replacement = LoaderPort();
        replacement.DeviceInstanceId = @"USB\VID_2207&PID_350B\6&2F0A3B7&1&2";

        Assert.False(DeviceController.MatchesRockchipLoaderEvidence(previous, replacement));
    }

    [Fact]
    public void MatchesRockchipLoaderEvidence_LocationUnprovenOnEitherSide_IsRefused()
    {
        var previous = MaskromPort();
        previous.DeviceInstanceId = @"USB\VID_2207&PID_350B";
        var replacement = LoaderPort();

        Assert.False(DeviceController.MatchesRockchipLoaderEvidence(previous, replacement));
    }

    [Fact]
    public void MatchesRockchipLoaderEvidence_BackendPortPath_MatchesWhenChainsUnavailable()
    {
        var previous = MaskromPort();
        previous.DeviceInstanceId = null;
        previous.PhysicalPortPath = "1-3-1";
        var replacement = LoaderPort();
        replacement.DeviceInstanceId = null;
        replacement.PhysicalPortPath = "1-3-1";

        Assert.True(DeviceController.MatchesRockchipLoaderEvidence(previous, replacement));
    }

    [Theory]
    [InlineData("ROCKCHIP", null, true)]
    [InlineData("rockchip", null, true)]
    [InlineData(" ROCKCHIP ", null, true)]
    [InlineData("0000000000", 0x0201, true)]
    [InlineData("0000000000", null, false)]
    [InlineData("ROCKCHIP2", 0x0201, true)]
    public void IsRockchipLoaderStage_SerialAndBcdUsb_DecideStageEvidence(
        string? serial, int? bcdUsbRaw, bool expected)
    {
        ushort? bcdUsb = bcdUsbRaw is { } raw ? (ushort)raw : null;
        var info = new UsbDeviceInfo { VendorId = 0x2207, ProductId = 0x350B, SerialNumber = serial, BcdUsb = bcdUsb };

        Assert.Equal(expected, DeviceController.IsRockchipLoaderStage(info));
    }

    [Fact]
    public void IsRockchipLoaderStage_InstanceTailRockchip_CountsAsStageEvidence()
    {
        var info = new UsbDeviceInfo
        {
            VendorId = 0x2207,
            ProductId = 0x350B,
            SerialNumber = null,
            BcdUsb = null,
            DeviceInstanceId = @"USB\VID_2207&PID_350B\ROCKCHIP",
        };

        Assert.True(DeviceController.IsRockchipLoaderStage(info));
    }

    [Fact]
    public void IsRockchipLoaderStage_InstanceTailLocationString_DoesNotCountAsStageEvidence()
    {
        var info = new UsbDeviceInfo
        {
            VendorId = 0x2207,
            ProductId = 0x350B,
            SerialNumber = null,
            BcdUsb = null,
            DeviceInstanceId = @"USB\VID_2207&PID_350B\6&2F0A3B7&0&2",
        };

        Assert.False(DeviceController.IsRockchipLoaderStage(info));
    }

    [Fact]
    public void IsRockchipLoaderStage_InstanceTailOtherSerial_DoesNotCountAsStageEvidence()
    {
        var info = new UsbDeviceInfo
        {
            VendorId = 0x2207,
            ProductId = 0x350B,
            SerialNumber = null,
            BcdUsb = null,
            DeviceInstanceId = @"USB\VID_2207&PID_350B\0000000000",
        };

        Assert.False(DeviceController.IsRockchipLoaderStage(info));
    }

    [Fact]
    public void RockchipObservationReplaced_SerialOverwrittenByLoaderName_ReportsReplacement()
    {
        var previous = MaskromPort();
        var listed = LoaderPort();

        Assert.True(DeviceController.RockchipObservationReplaced(previous, listed));
    }

    [Fact]
    public void RockchipObservationReplaced_ContainerChanged_SameSerial_ReportsReplacement()
    {
        var previous = MaskromPort();
        var listed = LoaderPort(serial: previous.SerialNumber, containerId: Guid.NewGuid());

        Assert.True(DeviceController.RockchipObservationReplaced(previous, listed));
    }

    [Fact]
    public void RockchipObservationReplaced_IdentityUnchanged_ReportsNoReplacement()
    {
        var previous = MaskromPort();
        var listed = LoaderPort(serial: previous.SerialNumber, containerId: previous.ContainerId);

        Assert.False(DeviceController.RockchipObservationReplaced(previous, listed));
    }

    [Fact]
    public void RockchipObservationReplaced_ListedObservationMissing_ReportsNoReplacement()
    {
        var previous = MaskromPort();

        Assert.False(DeviceController.RockchipObservationReplaced(previous, null));
    }
}