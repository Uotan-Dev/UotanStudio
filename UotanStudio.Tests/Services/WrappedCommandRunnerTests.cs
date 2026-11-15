using UotanStudio.Services.Devices;
using UotanStudio.Services.Terminal.Wrapped;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WrappedCommandRunnerTests
{
    private static WrappedCommandRunner CreateRunner(WrappedCommandTestGateway gateway)
        => new(gateway, "1.0.0-test");

    private static async Task<(int Exit, string Text)> RunAsync(WrappedCommandRunner runner, string line)
    {
        var output = new List<string>();
        var command = WrappedCommandLine.TryParse(line)!;
        var exit = await runner.RunAsync(command, text =>
        {
            lock (output)
            {
                output.Add(text);
            }
        }, TestContext.Current.CancellationToken).ConfigureAwait(false);
        string text2;
        lock (output)
        {
            text2 = string.Concat(output);
        }
        return (exit, text2);
    }

    [Fact]
    public void CanHandle_KnownAndUnknownSubcommands_GatesInterception()
    {
        var runner = CreateRunner(new WrappedCommandTestGateway());
        Assert.True(runner.CanHandle(WrappedCommandLine.TryParse("adb devices")!));
        Assert.True(runner.CanHandle(WrappedCommandLine.TryParse("fastboot getvar product")!));
        Assert.True(runner.CanHandle(WrappedCommandLine.TryParse("hdc list targets")!));
        Assert.True(runner.CanHandle(WrappedCommandLine.TryParse("hdc tmode port 10189")!));
        Assert.False(runner.CanHandle(WrappedCommandLine.TryParse("adb frobnicate")!));
        Assert.False(runner.CanHandle(WrappedCommandLine.TryParse("hdc bogus")!));
    }

    [Fact]
    public async Task RunAsync_AdbVersion_PrintsWrappedBanner()
    {
        var (exit, text) = await RunAsync(CreateRunner(new WrappedCommandTestGateway()), "adb version");
        Assert.Equal(0, exit);
        Assert.Contains("Android Debug Bridge version 1.0.41\r\n", text);
        Assert.Contains("Version UotanStudio Wrapped CommandLine Tool 1.0.0-test\r\n", text);
    }

    [Fact]
    public async Task RunAsync_AdbDevices_RendersOfficialTable()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "emulator-5554", "offline",
            DeviceTransportKind.Tcp));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "adb devices");
        Assert.Equal(0, exit);
        Assert.StartsWith("List of devices attached\r\n", text);
        Assert.Contains("R58M1234\tdevice\r\n", text);
        Assert.Contains("emulator-5554\toffline\r\n", text);
        Assert.DoesNotContain("fastboot:", text);
    }

    [Fact]
    public async Task RunAsync_AdbShellCommand_ExecutesThroughGateway()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        gateway.NextShellOutput = "level: 42\r\n";
        var (exit, text) = await RunAsync(CreateRunner(gateway), "adb shell dumpsys battery");
        Assert.Equal(0, exit);
        Assert.Equal("dumpsys battery", gateway.ShellCalls.Single().Command);
        Assert.Contains("level: 42\r\n", text);
    }

    [Fact]
    public async Task RunAsync_InteractiveAdbShell_OpensDeviceShellPanel()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "adb shell");
        Assert.Equal(0, exit);
        Assert.Equal("R58M1234", gateway.OpenedDeviceShells.Single().Serial);
        Assert.Empty(gateway.ShellCalls);
        Assert.Empty(text);
    }

    [Fact]
    public async Task RunAsync_AdbShellWithoutDevice_ReportsOfficialError()
    {
        var (exit, text) = await RunAsync(CreateRunner(new WrappedCommandTestGateway()), "adb shell");
        Assert.Equal(1, exit);
        Assert.Contains("adb: error: no devices/emulators found\r\n", text);
    }

    [Fact]
    public async Task RunAsync_AdbShellWithAmbiguousTargets_ReportsMoreThanOne()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M5678"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "adb shell");
        Assert.Equal(1, exit);
        Assert.Contains("adb: error: more than one device/emulator\r\n", text);
    }

    [Fact]
    public async Task RunAsync_AdbPushMissingFile_ReportsStatFailure()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        var missing = Path.Combine(Path.GetTempPath(), $"uotan-wrapped-{Guid.NewGuid():N}.bin");
        var (exit, text) = await RunAsync(CreateRunner(gateway), $"adb push {missing} /data/local/tmp/");
        Assert.Equal(1, exit);
        Assert.Contains($"adb: error: cannot stat '{missing}': No such file or directory", text);
        Assert.Empty(gateway.PushCalls);
    }

    [Fact]
    public async Task RunAsync_AdbInstall_PushesAndRunsPackageManager()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        var runner = CreateRunner(gateway);
        var apk = Path.Combine(Path.GetTempPath(), $"uotan-wrapped-{Guid.NewGuid():N}.apk");
        await File.WriteAllTextAsync(apk, "apk", TestContext.Current.CancellationToken);
        try
        {
            var (exit, text) = await RunAsync(runner, $"adb install -r \"{apk}\"");
            Assert.Equal(0, exit);
            Assert.Equal("-r", gateway.InstallOptions.Single());
            Assert.Equal("Success\r\n", text);
            Assert.Contains(apk, gateway.PushCalls.Single().Local);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task RunAsync_AdbRebootBootloader_MapsToRebootMode()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "adb reboot-bootloader");
        Assert.Equal(0, exit);
        Assert.Equal(("R58M1234", "reboot:bootloader"), gateway.AdbServiceCalls.Single());
        Assert.Empty(text);
    }

    [Fact]
    public async Task RunAsync_AdbConnectWithoutDevice_ReportsCannotConnect()
    {
        var (exit, text) = await RunAsync(CreateRunner(new WrappedCommandTestGateway()), "adb connect 192.168.1.50");
        Assert.Equal(1, exit);
        Assert.StartsWith("adb: error: cannot connect to 192.168.1.50:5555:", text);
    }

    [Fact]
    public async Task RunAsync_AdbKillServer_StopsServerQuietly()
    {
        var gateway = new WrappedCommandTestGateway();
        var (exit, text) = await RunAsync(CreateRunner(gateway), "adb kill-server");
        Assert.Equal(0, exit);
        Assert.Equal(["adb"], gateway.StopServerCalls);
        Assert.Empty(text);
    }

    [Fact]
    public async Task RunAsync_FastbootGetVar_PrintsBootloaderPrefix()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Fastboot, "ABC123", "fastboot"));
        gateway.FastbootVariables["product"] = "smith";
        var (exit, text) = await RunAsync(CreateRunner(gateway), "fastboot getvar product");
        Assert.Equal(0, exit);
        Assert.Contains("(bootloader) product: smith\r\n", text);
        Assert.Matches("Finished\\. Total time: 0\\.\\d{3}s\r\n", text);
    }

    [Fact]
    public async Task RunAsync_FastbootGetVarAll_ListsEveryVariable()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Fastboot, "ABC123", "fastboot"));
        gateway.FastbootVariables["product"] = "smith";
        gateway.FastbootVariables["current-slot"] = "a";
        var (exit, text) = await RunAsync(CreateRunner(gateway), "fastboot getvar all");
        Assert.Equal(0, exit);
        Assert.Contains("(bootloader) product: smith\r\n", text);
        Assert.Contains("(bootloader) current-slot: a\r\n", text);
    }

    [Fact]
    public async Task RunAsync_FastbootDevices_RendersFastbootRows()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Fastboot, "ABC123", "fastboot"));
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "fastboot devices");
        Assert.Equal(0, exit);
        Assert.Equal("ABC123\tfastboot\r\n", text);
    }

    [Fact]
    public async Task RunAsync_FastbootErase_SendsCommandWithOfficialLayout()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Fastboot, "ABC123", "fastboot"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "fastboot erase boot");
        Assert.Equal(0, exit);
        Assert.Equal("erase:boot", gateway.FastbootCommands.Single());
        Assert.Matches("Erasing 'boot'\\s+OKAY \\[\\s*\\d+\\.\\d{3}s\\]\r\n", text);
        Assert.Contains("Finished. Total time:", text);
    }

    [Fact]
    public async Task RunAsync_FastbootFlashMissingFile_ReportsReadFailure()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Fastboot, "ABC123", "fastboot"));
        var missing = Path.Combine(Path.GetTempPath(), $"uotan-wrapped-{Guid.NewGuid():N}.img");
        var (exit, text) = await RunAsync(CreateRunner(gateway), $"fastboot flash boot {missing}");
        Assert.Equal(1, exit);
        Assert.Contains($"fastboot: error: could not read image '{missing}'", text);
        Assert.Empty(gateway.FastbootCommands);
    }

    [Fact]
    public async Task RunAsync_FastbootWithoutDevice_ReportsOfficialError()
    {
        var (exit, text) = await RunAsync(CreateRunner(new WrappedCommandTestGateway()), "fastboot reboot");
        Assert.Equal(1, exit);
        Assert.Contains("fastboot: error: no devices\r\n", text);
    }

    [Fact]
    public async Task RunAsync_HdcListTargets_PrintsConnectKeys()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Hdc, "127.0.0.1:5555"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "hdc list targets");
        Assert.Equal(0, exit);
        Assert.Equal("127.0.0.1:5555\r\n", text);
    }

    [Fact]
    public async Task RunAsync_HdcShellCommand_ExecutesThroughGateway()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Hdc, "127.0.0.1:5555"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "hdc shell aa start -a MainAbility");
        Assert.Equal(0, exit);
        Assert.Equal("aa start -a MainAbility", gateway.ShellCalls.Single().Command);
        Assert.Contains("hdc shell output", text);
    }

    [Fact]
    public async Task RunAsync_InteractiveHdcShell_OpensDeviceShellPanel()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Hdc, "127.0.0.1:5555"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "hdc shell");
        Assert.Equal(0, exit);
        Assert.Single(gateway.OpenedDeviceShells);
        Assert.Empty(gateway.ShellCalls);
        Assert.Empty(text);
    }

    [Fact]
    public async Task RunAsync_HdcFileSend_ReportsOfficialTransferLine()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Hdc, "127.0.0.1:5555"));
        var local = Path.Combine(Path.GetTempPath(), $"uotan-wrapped-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(local, "payload", TestContext.Current.CancellationToken);
        try
        {
            var (exit, text) = await RunAsync(CreateRunner(gateway), $"hdc file send {local} /data/local/tmp/payload.bin");
            Assert.Equal(0, exit);
            Assert.Matches("FileTransfer finish, Size:7, File count = 1, time:\\d+ms rate:\\d+\\.\\d{2}kB/s\r\n", text);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public async Task RunAsync_HdcInstall_PrintsInstallerOutput()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Hdc, "127.0.0.1:5555"));
        var hap = Path.Combine(Path.GetTempPath(), $"uotan-wrapped-{Guid.NewGuid():N}.hap");
        await File.WriteAllTextAsync(hap, "hap", TestContext.Current.CancellationToken);
        try
        {
            var (exit, text) = await RunAsync(CreateRunner(gateway), $"hdc install -r {hap}");
            Assert.Equal(0, exit);
            Assert.Contains("install bundle successfully\r\n", text);
        }
        finally
        {
            File.Delete(hap);
        }
    }

    [Fact]
    public async Task RunAsync_UnsupportedOfficialCommand_ReportsErrorWithExitOne()
    {
        var (exit, text) = await RunAsync(CreateRunner(new WrappedCommandTestGateway()),
            "adb forward tcp:8080 tcp:80");
        Assert.Equal(1, exit);
        Assert.Contains("adb: error: forward is not supported by the wrapped command line tool\r\n", text);
    }

    [Fact]
    public async Task RunAsync_AdbLogcat_StreamsRawDeviceOutput()
    {
        var gateway = new WrappedCommandTestGateway();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        var (exit, text) = await RunAsync(CreateRunner(gateway), "adb logcat -d");
        Assert.Equal(0, exit);
        Assert.Equal("logcat -d", gateway.ShellCalls.Single().Command);
        Assert.Contains("streamed", text);
    }
}