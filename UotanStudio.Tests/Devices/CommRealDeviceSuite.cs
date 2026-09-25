using System.Security.Cryptography;
using UotanStudio.Comm;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Opt-in suite against a real adb-connected device. Set <c>UOTAN_TEST_ADB_TARGET</c> to the
/// server serial of the device to enable; every test in this file is skipped otherwise.
/// Reboot scenarios additionally require <c>UOTAN_TEST_ALLOW_REBOOT=1</c> because they take
/// the device offline for the duration of the run. Mirroring requires a scrcpy-server jar
/// (see <see cref="LocateServerJar"/>). No flash, erase, unlock or data-mutating fastboot
/// operation is performed; only read-only getvar calls.
/// <para>针对真实 adb 设备的选配测试套件。设置 <c>UOTAN_TEST_ADB_TARGET</c> 后启用；
/// 重启场景还需 <c>UOTAN_TEST_ALLOW_REBOOT=1</c>。投屏需要 scrcpy-server jar。
/// fastboot 仅执行只读 getvar，不做任何刷写/擦除/解锁/数据修改操作。</para>
/// </summary>
public sealed class CommRealDeviceSuite
{
    static CommRealDeviceSuite()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("UOTAN_TEST_USB_BACKEND"), "libusb", StringComparison.Ordinal))
        {
            return;
        }

        var nativeLibrary = Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dll");
        if (!File.Exists(nativeLibrary))
        {
            throw new InvalidOperationException("libusb backend requested but the native runtime is missing from the test output.");
        }

        UotanStudio.Comm.CommKit.SetLibusbLibraryPath(nativeLibrary);
        UotanStudio.Comm.DeviceCommunicationConfiguration.Current =
            UotanStudio.Comm.DeviceCommunicationConfiguration.Current with
            { UsbBackend = UotanStudio.Comm.Transports.Usb.Abstractions.UsbApiKind.LibUsbDotNet };
    }

    private readonly DeviceController _controller = DeviceControllerFactory.CreateDefault();

    /// <summary>Accepts either an adb server serial or a Windows USB instance path
    /// (USB\VID_...\PID_...\...) so the suite can also prove USB-direct discovery.
    /// <para>接受 adb server serial 或 Windows USB 实例路径，使套件也能验证 USB 直连发现。</para></summary>
    private string Serial => Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_TARGET") ?? string.Empty;

    private string? UsbInstanceId => Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");

    private void RequireTarget()
    {
        if (string.IsNullOrWhiteSpace(Serial))
        {
            Assert.Skip("UOTAN_TEST_ADB_TARGET is not set; real-device suite is opt-in.");
        }
    }

    private async Task<DeviceLease> LeaseDeviceAsync()
    {
        var snapshot = await _controller.RefreshAsync(TestContext.Current.CancellationToken);
        var endpoint = snapshot.Endpoints.FirstOrDefault(candidate =>
            candidate.Protocol == "adb" && candidate.Address == Serial)
            ?? throw new InvalidOperationException($"Target '{Serial}' is not present in the server snapshot.");
        var session = AdbSession.ForServer(endpoint.Address, UotanStudio.Comm.Routing.DebugBridgeEndpoints.ServerHost,
            UotanStudio.Comm.Routing.DebugBridgeEndpoints.AdbServerPort);
        session.Connect();
        return _controller.AdoptSession(endpoint, session);
    }

    [Fact]
    public async Task Discovery_ServerLive_SuppressesDirectUsbAdbEndpoints()
    {
        RequireTarget();
        var snapshot = await _controller.RefreshAsync(TestContext.Current.CancellationToken);
        var target = snapshot.Endpoints.FirstOrDefault(candidate => candidate.Address == Serial);
        Assert.NotNull(target);
        Assert.Equal("adb", target!.Protocol);
        Assert.DoesNotContain(snapshot.Endpoints, candidate =>
            candidate.Route == "direct-usb" && candidate.Protocol == "adb");
    }

    [Fact]
    public async Task ServerSession_ShellCommand_ReturnsOutputAndExitCode()
    {
        RequireTarget();
        await using var lease = await LeaseDeviceAsync();
        Assert.True(lease.TryGetSession<IAdbDeviceSession>(out var session));
        var result = session.ExecuteCommand("echo uotan-ok");
        Assert.Contains("uotan-ok", System.Text.Encoding.UTF8.GetString(result.Stdout));
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task InteractiveShellStream_DeliversIncrementalOutputBeforeClose()
    {
        RequireTarget();
        await using var lease = await LeaseDeviceAsync();
        Assert.True(lease.TryGetSession<IAdbDeviceSession>(out var session));
        using var stream = session.OpenStream("shell:echo line-one; sleep 1; echo line-two");
        var received = new List<string>();
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline && !received.Contains("line-two"))
        {
            var chunk = stream.Read(2000);
            if (chunk is { Length: > 0 })
            {
                var text = System.Text.Encoding.UTF8.GetString(chunk);
                received.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim('\r').Trim()));
            }
        }

        Assert.Contains("line-one", received);
        Assert.Contains("line-two", received);
    }

    [Fact]
    public async Task PushPullFile_RoundTrip_PreservesBytes()
    {
        RequireTarget();
        await using var lease = await LeaseDeviceAsync();
        Assert.True(lease.TryGetSession<IAdbDeviceSession>(out var session));
        var sync = session.CreateSyncClient();
        var remote = "/data/local/tmp/uotan_suite_roundtrip.bin";
        var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 12345);
        var local = Path.Combine(Path.GetTempPath(), $"uotan_suite_{Guid.NewGuid():N}.bin");
        var pulled = local + ".pull";
        try
        {
            await File.WriteAllBytesAsync(local, payload, TestContext.Current.CancellationToken);
            sync.Push(local, remote);
            sync.Pull(remote, pulled);
            var roundTrip = await File.ReadAllBytesAsync(pulled, TestContext.Current.CancellationToken);
            Assert.Equal(Convert.ToHexString(MD5.HashData(payload)),
                Convert.ToHexString(MD5.HashData(roundTrip)));
        }
        finally
        {
            try { session.ExecuteCommand($"rm -f {remote}"); } catch { }
            File.Delete(local);
            if (File.Exists(pulled)) File.Delete(pulled);
        }
    }

    [Fact]
    public async Task DeviceVariables_PopulateStaticCache_AndSurviveSecondRead()
    {
        RequireTarget();
        await using var lease = await LeaseDeviceAsync();
        Assert.True(lease.TryGetSession<IAdbDeviceSession>(out var session));

        IReadOnlyDictionary<string, string>? cached = _controller.Properties.Get(Serial);
        if (cached is null)
        {
            var model = System.Text.Encoding.UTF8.GetString(session.ExecuteCommand("getprop ro.product.model").Stdout).Trim();
            var release = System.Text.Encoding.UTF8.GetString(session.ExecuteCommand("getprop ro.build.version.release").Stdout).Trim();
            var kernel = System.Text.Encoding.UTF8.GetString(session.ExecuteCommand("uname -r").Stdout).Trim();
            Assert.False(string.IsNullOrWhiteSpace(model));
            _controller.Properties.Store(Serial, new Dictionary<string, string>
            {
                ["model"] = model,
                ["androidRelease"] = release,
                ["kernelVersion"] = kernel
            });
            cached = _controller.Properties.Get(Serial);
        }

        Assert.NotNull(cached);
        Assert.False(string.IsNullOrWhiteSpace(cached!["model"]));
        Assert.Same(cached, _controller.Properties.Get(Serial));
    }

    [Fact]
    public async Task Mirror_StartServerAndDecodeFirstFrame_ThenStop()
    {
        RequireTarget();
        var jar = LocateServerJar();
        if (jar is null)
        {
            Assert.Skip($"scrcpy-server {UotanStudio.Services.Mirroring.ScrcpyConstants.ServerVersion} jar not found.");
        }

        await using var lease = await LeaseDeviceAsync();
        Assert.True(lease.TryGetSession<IAdbDeviceSession>(out var session));
        var options = new ScrcpyServerOptions { MaxSize = 512, MaxFps = 15, Video = true, Audio = false };
        ScrcpyServerManager.PushServer(session, jar);
        var shell = ScrcpyServerManager.StartServer(session, options);
        using var client = new ScrcpyClient(session, options);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    client.Connect(3000);
                    break;
                }
                catch (Exception) when (attempt < 19)
                {
                    Thread.Sleep(250);
                }
            }
            var deadline = Environment.TickCount64 + 20000;
            ScrcpyVideoFrame? frame = null;
            while (Environment.TickCount64 < deadline)
            {
                frame = client.ReadFrame(2000);
                if (frame is not null)
                {
                    break;
                }
            }

            Assert.NotNull(frame);
            Assert.True(frame!.Data.Length > 0);
            Assert.NotEqual(0u, client.VideoMetadata?.CodecFourCc ?? 0);
        }
        finally
        {
            ScrcpyServerManager.StopServer(session);
        }
    }

    [Fact]
    public async Task Reboot_FastbootVariables_RebootBackToSystem()
    {
        RequireTarget();
        if (!string.Equals(Environment.GetEnvironmentVariable("UOTAN_TEST_ALLOW_REBOOT"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("UOTAN_TEST_ALLOW_REBOOT!=1; reboot scenarios stay opt-in.");
        }

        await using (var lease = await LeaseDeviceAsync())
        {
            Assert.True(lease.TryGetSession<IAdbDeviceSession>(out var session));
            session.ExecuteCommand("reboot bootloader");
        }

        var kit = CommKit.Default;
        var switched = await kit.WaitForModeSwitchAsync(
            removedFilter: null,
            appearedFilter: new UsbDeviceFilter { VendorId = 0x18D1 },
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(switched, "The device did not reach fastboot mode within the timeout.");

        var chainDeadline = Environment.TickCount64 + 60000;
        while (true)
        {
            try
            {
                var snapshot = await _controller.RefreshAsync(TestContext.Current.CancellationToken);
                var fastbootEndpoint = snapshot.Endpoints.FirstOrDefault(candidate =>
                    candidate.Route == "direct-usb" && candidate.Protocol == "fastboot")
                    ?? throw new InvalidOperationException("No fastboot endpoint was discovered after reboot.");
                await using (var lease = await _controller.OpenAsync(snapshot, fastbootEndpoint.Id,
                    DeviceSessionKind.Fastboot, TestContext.Current.CancellationToken))
                {
                    Assert.True(lease.TryGetSession<UotanStudio.Comm.Protocols.Fastboot.FastbootSession>(out var fastboot));
                    var product = fastboot.GetVariable("product");
                    Assert.False(string.IsNullOrWhiteSpace(product));
                    var serial = fastboot.GetVariable("serialno");
                    if (!string.IsNullOrWhiteSpace(serial))
                    {
                        Assert.True(string.Equals(serial, Serial, StringComparison.Ordinal) ||
                            string.Equals(serial, Serial, StringComparison.OrdinalIgnoreCase),
                            $"fastboot serialno '{serial}' does not match the server serial '{Serial}'.");
                    }

                    _ = fastboot.GetVariable("current-slot");
                    _ = fastboot.GetVariable("max-download-size");
                    _ = fastboot.GetVariable("battery-soc-ok");
                    fastboot.Reboot();
                }

                break;
            }
            catch (Exception ex) when (Environment.TickCount64 < chainDeadline &&
                ex is IOException or InvalidOperationException or NotSupportedException)
            {
                await Task.Delay(2000, TestContext.Current.CancellationToken);
            }
        }

        var backDeadline = Environment.TickCount64 + (int)TimeSpan.FromMinutes(3).TotalMilliseconds;
        while (Environment.TickCount64 < backDeadline)
        {
            await Task.Delay(3000, TestContext.Current.CancellationToken);
            try
            {
                var refreshed = await _controller.RefreshAsync(TestContext.Current.CancellationToken);
                if (refreshed.Endpoints.Any(candidate => candidate.Protocol == "adb" && candidate.Address == Serial))
                {
                    return;
                }
            }
            catch { }
        }

        Assert.Fail("The device did not return to adb within the timeout after fastboot reboot.");
    }

    private static string? LocateServerJar()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("UOTAN_TEST_SCRCPY_JAR"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "scrcpy-server.jar"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "UotanStudio", "Assets", "scrcpy-server.jar")
        };
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }
}