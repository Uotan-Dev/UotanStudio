using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Checks that the device badge separates the two SpacemiT fastboot stages instead of collapsing them into one
/// label, while every other fastboot endpoint keeps the generic text.
/// <para>校验设备徽章区分进迭时空的两个 fastboot 阶段，而不是把它们折叠成一个标签，同时其它 fastboot 端点保持
/// 通用文本。</para>
/// </summary>
public sealed class SpacemiTBootStageDisplayTests
{
    [Theory]
    [InlineData(SpacemiTBootStage.BromFdl, "SpacemiT · FDL")]
    [InlineData(SpacemiTBootStage.Uboot, "SpacemiT · U-Boot")]
    [InlineData(SpacemiTBootStage.Unknown, "SpacemiT")]
    public void FastbootBadge_SpacemiTGadget_ShowsTheBootStage(SpacemiTBootStage stage, string expected)
    {
        var device = Create(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, stage);

        Assert.Equal(expected, device.FastbootBadge);
    }

    [Fact]
    public void FastbootBadge_OtherVendor_KeepsTheGenericLabel()
    {
        var device = Create(0x18D1, 0x4EE7, SpacemiTBootStage.Uboot);

        Assert.False(device.IsSpacemiTFastboot);
        Assert.Equal("Fastboot", device.FastbootBadge);
    }

    [Fact]
    public void StateLabel_SpacemiTGadget_KeepsTheReportedFastbootState()
    {
        var device = Create(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, SpacemiTBootStage.BromFdl);
        device.Slot = "Slot A";

        Assert.Equal("fastboot Slot A", device.StateLabel);
    }

    [Fact]
    public void FastbootStage_StaysUnknown_ForNonSpacemiTEndpoint()
    {
        var device = Create(0x18D1, 0x4EE7, SpacemiTBootStage.Unknown);

        Assert.Equal(SpacemiTBootStage.Unknown, device.FastbootStage);
    }

    private static DeviceInfo Create(ushort vendorId, ushort productId, SpacemiTBootStage stage)
        => new(new DetectedDevice
        {
            Key = "usb:test",
            Serial = "10000030",
            Protocol = DeviceProtocolKind.Fastboot,
            Transport = DeviceTransportKind.Usb,
            State = "fastboot",
            VendorId = vendorId,
            ProductId = productId,
            FastbootStage = stage
        });
}