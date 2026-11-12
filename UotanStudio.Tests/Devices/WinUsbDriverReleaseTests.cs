using System.Runtime.Versioning;
using UotanStudio.Comm.Transports.Usb.Drivers;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Verifies how ending a USB takeover decides its reported outcome: only a device observed without the
/// takeover binding counts as released, and a pending reboot or a still-bound devnode is reported as a
/// restart requirement instead of a success.
/// <para>验证结束 USB 接管时上报结果的判定：只有观测到设备不再带接管绑定才算解除完成；需要重启或设备节点
/// 仍带接管绑定时报为需要重启，绝不报成功。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinUsbDriverReleaseTests
{
    [Fact]
    public void ClassifyReleaseOutcome_VerifiedRelease_ReportsSucceeded()
    {
        Assert.Equal(WinUsbDriverOperationStatus.Succeeded,
            WinUsbDriverInstaller.ClassifyReleaseOutcome(rebootRequired: false, bindingReleased: true));
    }

    [Fact]
    public void ClassifyReleaseOutcome_BindingStillTakeover_ReportsRestartRequired()
    {
        Assert.Equal(WinUsbDriverOperationStatus.RestartRequired,
            WinUsbDriverInstaller.ClassifyReleaseOutcome(rebootRequired: false, bindingReleased: false));
    }

    [Fact]
    public void ClassifyReleaseOutcome_PendingReboot_ReportsRestartRequired()
    {
        Assert.Equal(WinUsbDriverOperationStatus.RestartRequired,
            WinUsbDriverInstaller.ClassifyReleaseOutcome(rebootRequired: true, bindingReleased: true));
    }

    [Fact]
    public void WaitForBindingRelease_BindingLeavesAfterTwoObservations_ReturnsTrue()
    {
        var observations = 0;

        var released = WinUsbDriverInstaller.WaitForBindingRelease(
            () => ++observations < 3
                ? TakeoverBoundState()
                : new WinUsbDriverState { DevicePresent = true, IsTakeoverBinding = false },
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));

        Assert.True(released);
        Assert.Equal(3, observations);
    }

    [Fact]
    public void WaitForBindingRelease_DevnodeStillTakeoverBound_ReturnsFalseAfterTimeout()
    {
        var observations = 0;

        var released = WinUsbDriverInstaller.WaitForBindingRelease(
            () =>
            {
                observations++;
                return TakeoverBoundState();
            },
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));

        Assert.False(released);
        Assert.True(observations > 1, $"Expected repeated polling, saw {observations} observation(s).");
    }

    [Fact]
    public void WaitForBindingRelease_DevnodeNoLongerLocatable_ReturnsTrue()
    {
        var released = WinUsbDriverInstaller.WaitForBindingRelease(
            () => new WinUsbDriverState { DevicePresent = false },
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));

        Assert.True(released);
    }

    [Theory]
    [InlineData("oem12.inf")]
    [InlineData("oem9.INF")]
    [InlineData("OEM1234.Inf")]
    public void IsOwnStagedInfName_BareStagedName_ReturnsTrue(string infName)
    {
        Assert.True(WinUsbDriverInstaller.IsOwnStagedInfName(infName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("winusb.inf")]
    [InlineData("oem12.inf.bak")]
    [InlineData("oem.inf ")]
    [InlineData(@"C:\Windows\INF\oem12.inf")]
    [InlineData(@"..\oem12.inf")]
    [InlineData("oem12.inf:stream")]
    public void IsOwnStagedInfName_NotBareStagedName_ReturnsFalse(string? infName)
    {
        Assert.False(WinUsbDriverInstaller.IsOwnStagedInfName(infName));
    }

    [Fact]
    public void IsOwnStagedInfName_InvalidFileNameCharacter_ReturnsFalse()
    {
        Assert.False(WinUsbDriverInstaller.IsOwnStagedInfName("oem12?.inf"));
    }

    private static WinUsbDriverState TakeoverBoundState() => new()
    {
        DevicePresent = true,
        DriverService = "WINUSB",
        DriverProvider = WinUsbSelfSignedDriverPackage.ProviderName,
        IsTakeoverBinding = true
    };
}