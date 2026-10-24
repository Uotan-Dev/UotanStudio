using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class HdcMirrorStartGateTests
{
    [Fact]
    public void TryEnterHdcStartGate_SecondCallWithoutWaiting_IsRejected()
    {
        var key = "usb|gate-" + Guid.NewGuid().ToString("N");
        Assert.True(DeviceMonitorService.TryEnterHdcStartGate(key, 0));
        try
        {
            Assert.False(DeviceMonitorService.TryEnterHdcStartGate(key, 0));
        }
        finally
        {
            DeviceMonitorService.ExitHdcStartGate(key);
        }
    }

    [Fact]
    public void ExitHdcStartGate_AfterRejectedSecondStart_AllowsTheNextStart()
    {
        var key = "usb|gate-" + Guid.NewGuid().ToString("N");
        Assert.True(DeviceMonitorService.TryEnterHdcStartGate(key, 0));
        Assert.False(DeviceMonitorService.TryEnterHdcStartGate(key, 0));
        DeviceMonitorService.ExitHdcStartGate(key);

        Assert.True(DeviceMonitorService.TryEnterHdcStartGate(key, 0));
        DeviceMonitorService.ExitHdcStartGate(key);
    }

    [Fact]
    public void TryEnterHdcStartGate_DifferentTargets_DoNotBlockEachOther()
    {
        var first = "usb|gate-" + Guid.NewGuid().ToString("N");
        var second = "usb|gate-" + Guid.NewGuid().ToString("N");
        Assert.True(DeviceMonitorService.TryEnterHdcStartGate(first, 0));
        try
        {
            Assert.True(DeviceMonitorService.TryEnterHdcStartGate(second, 0));
            DeviceMonitorService.ExitHdcStartGate(second);
        }
        finally
        {
            DeviceMonitorService.ExitHdcStartGate(first);
        }
    }

    [Fact]
    public void TryEnterHdcStartGate_BlankKey_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = DeviceMonitorService.TryEnterHdcStartGate(" ", 0);
        });
        Assert.Throws<ArgumentException>(() => DeviceMonitorService.ExitHdcStartGate(" "));
    }
}