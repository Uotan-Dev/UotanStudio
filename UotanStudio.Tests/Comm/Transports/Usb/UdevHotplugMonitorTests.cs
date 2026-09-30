using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class UdevHotplugMonitorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, LinuxUsbAPI.EACCES)]
    [InlineData(0, LinuxUsbAPI.EINTR)]
    [InlineData(-1, LinuxUsbAPI.EINTR)]
    public void ShouldRetryAfterReceive_InterruptedOrEmptyReceive_KeepsLoopAlive(int received, int lastError)
        => Assert.True(UdevHotplugMonitor.ShouldRetryAfterReceive(received, lastError));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-1, LinuxUsbAPI.EAGAIN)]
    [InlineData(-1, LinuxUsbAPI.EINTR + 1)]
    public void ShouldRetryAfterReceive_RealSocketFailure_EndsLoop(int received, int lastError)
        => Assert.False(UdevHotplugMonitor.ShouldRetryAfterReceive(received, lastError));
}