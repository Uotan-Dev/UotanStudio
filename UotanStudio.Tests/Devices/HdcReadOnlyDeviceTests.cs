using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>Opt-in checks that never mutate the device. 显式启用、不会修改设备的检查。</summary>
public sealed class HdcReadOnlyDeviceTests
{
    [Fact]
    public async Task ConnectedDevice_ManagedChannelMatchesOfficialClient_AndReadsLogs()
    {
        var key = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_TARGET");
        var executable = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_EXE");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(executable))
            Assert.Skip("Set UOTAN_TEST_HDC_TARGET and UOTAN_TEST_HDC_EXE to enable read-only device validation.");
        var ct = TestContext.Current.CancellationToken;
        using var client = new HdcServerClient();
        var targets = await Task.Run(() => client.ListTargets(), ct);
        Assert.Contains(key, targets);
        var details = await Task.Run(() => client.ListTargetDetails(), ct);
        var target = Assert.Single(details, target => target.ConnectKey == key);
        var officialDetails = await HdcCommandClient.RunAsync(executable, ["list", "targets", "-v"], ct);
        Assert.Contains(target, HdcTargetInfo.Parse(officialDetails));
        var device = new DetectedDevice
        {
            Key = "hdc:" + key,
            Serial = key,
            Protocol = DeviceProtocolKind.Hdc,
            Transport = DeviceTransportKind.Tcp,
            State = "device"
        };
        var service = new HdcDeviceService(executable);
        foreach (var property in new[] { "const.product.model", "const.ohos.fullname", "const.ohos.apiversion" })
        {
            var official = await HdcCommandClient.RunAsync(executable, ["-t", key, "shell", "param", "get", property], ct);
            var managed = await service.GetPropertyAsync(device, property, ct);
            Assert.NotEmpty(managed.Trim());
            Assert.Equal(official.Trim(), managed.Trim());
        }
        Assert.Equal("uid=", (await service.ShellAsync(device, "id", ct)).Trim()[..4]);
        Assert.Equal("中文 test", await service.ShellAsync(device, "printf '中文 test'", ct));
        Assert.Equal("first\nlast", await service.ShellAsync(device, "printf 'first\\n'; sleep 1; printf last", ct));
        await Assert.ThrowsAsync<IOException>(() => service.ShellAsync(device, "exit 7", ct));
        // HDC shell does not carry an exit code: raw session clients report unknown (-1).
        using (var session = client.CreateSession(key))
        {
            await session.ConnectAsync(ct);
            var result = await Task.Run(() => session.ExecuteCommand("echo ready"), ct);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal("ready", Encoding.UTF8.GetString(result.Stdout).Trim());
        }
        var log = await client.ExecuteAsync(key, "shell hilog -a 2 -v year", cancellationToken: ct);
        var lines = Encoding.UTF8.GetString(log).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, line => AdbLogcatEntry.Parse(line.TrimEnd('\r')).Level is not null);
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(ct);
        duration.CancelAfter(TimeSpan.FromSeconds(2));
        long received = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteStreamingAsync(
            key, "shell hilog -v year", bytes => received += bytes.Length, duration.Token));
        Assert.True(received > 0);
        // Verify cancellation did not damage a subsequent target channel.
        Assert.Equal("alive", await service.ShellAsync(device, "echo alive", ct));
    }

    [Fact]
    public async Task ConnectedDevice_ReceiveReadsOnlyDeviceFile_AndPreservesContents()
    {
        var key = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_TARGET");
        var executable = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_EXE");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(executable))
            Assert.Skip("Read-only HDC hardware check is opt-in.");
        var ct = TestContext.Current.CancellationToken;
        var device = new DetectedDevice
        {
            Key = "hdc:" + key,
            Serial = key,
            Protocol = DeviceProtocolKind.Hdc,
            Transport = DeviceTransportKind.Tcp,
            State = "device"
        };
        var service = new HdcDeviceService(executable);
        // A regular, public system file; local destination deliberately includes spaces.
        const string remote = "/system/bin/toybox";
        var expected = (await service.ShellAsync(device, "sha256sum " + remote, ct)).Split(' ')[0];
        var folder = Path.Combine(Path.GetTempPath(), "uotan hdc " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var destination = Path.Combine(folder, "received toybox.bin");
            await service.ReceiveFileAsync(device, remote, destination, ct);
            await using var file = File.OpenRead(destination);
            Assert.Equal(expected, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}