using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class HdcDeviceServiceTests
{
    [Theory]
    [InlineData("[Info]App install path:a.hap msg:error: failed to install bundle.", false)]
    [InlineData("[Info]App uninstall path: msg:error: failed to uninstall bundle. code:9568386", true)]
    [InlineData("[Info]transfer complete", false)]
    public void InfoOutput_IsNotApplicationSuccess(string output, bool uninstall)
        => Assert.Throws<IOException>(() => HdcDeviceService.RequireApplicationSuccess(output, uninstall));

    [Theory]
    [InlineData("[Info]App install path:a.hap msg:install bundle successfully. AppMod finish", false)]
    [InlineData("[Info]App uninstall path: msg:uninstall bundle successfully. AppMod finish", true)]
    public void ApplicationResult_MatchesUpstreamFixtures(string output, bool uninstall)
        => HdcDeviceService.RequireApplicationSuccess(output, uninstall);

    [Fact]
    public async Task InvalidTargetAndBundle_AreRejectedBeforeStartingHdc()
    {
        var service = new HdcDeviceService();
        var device = new DetectedDevice { Key = "test", Serial = "test", Protocol = DeviceProtocolKind.Adb, Transport = DeviceTransportKind.Tcp };
        await Assert.ThrowsAsync<ArgumentException>(() => service.ShellAsync(device, "true", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UninstallAsync(device, "-h;anything", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ForwardAsync(device, 0, 80, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessRunner_DrainsBothPipesAndRejectsFailureWithZeroExitCode()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Uses the system Windows PowerShell as a fake HDC process.");
        var ct = TestContext.Current.CancellationToken;
        var output = await HdcCommandClient.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('x' * 20000); [Console]::Error.Write('y' * 20000)"], ct, 10000);
        Assert.Equal(40000, output.Length);
        await Assert.ThrowsAsync<IOException>(() => HdcCommandClient.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('[Fail]denied'); exit 0"], ct, 10000));
    }

    [Fact]
    public async Task ProcessRunner_TimeoutTerminatesChildInsteadOfReturningSuccess()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Uses the system Windows PowerShell as a fake HDC process.");
        await Assert.ThrowsAsync<TimeoutException>(() => HdcCommandClient.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], TestContext.Current.CancellationToken, 500));
    }
}
