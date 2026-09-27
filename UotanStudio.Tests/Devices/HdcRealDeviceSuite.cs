using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Opt-in suite against a real HDC-connected HarmonyOS device. Set
/// <c>UOTAN_TEST_HDC_TARGET</c> to the connect key (e.g. <c>5XM0225C18001159</c>) to enable;
/// every test in this file is skipped otherwise. The reboot scenario additionally requires
/// <c>UOTAN_TEST_ALLOW_REBOOT=1</c>. Only read-only queries are issued; nothing is installed,
/// uninstalled or erased.
/// <para>针对真实 HDC 设备的选配测试套件。设置 <c>UOTAN_TEST_HDC_TARGET</c> 后启用；
/// 重启场景还需 <c>UOTAN_TEST_ALLOW_REBOOT=1</c>。仅执行只读查询，不做任何安装/卸载/擦除。
/// 注意：本套件必须单独一次调用运行（与其他套件合跑时，多个控制器的 server 监视通道会
/// 超出 hdc server 的并发连接容忍度，造成无谓失败）。</para>
/// </summary>
public sealed class HdcRealDeviceSuite
{
    private static string Serial => Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_TARGET") ?? string.Empty;

    private static void RequireTarget()
    {
        if (string.IsNullOrWhiteSpace(Serial))
        {
            Assert.Skip("UOTAN_TEST_HDC_TARGET is not set; real-device suite is opt-in.");
        }
    }

    private static async Task<(DeviceController Controller, DeviceLease Lease, IHdcDeviceSession Session)> LeaseDeviceAsync()
    {
        var controller = DeviceControllerFactory.CreateDefault();
        try
        {
            var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
            var endpoint = snapshot.Endpoints.FirstOrDefault(candidate =>
                candidate.Protocol == "hdc" && candidate.Address == Serial)
                ?? throw new InvalidOperationException($"HDC target '{Serial}' is not present in the server snapshot.");
            var lease = await controller.OpenAsync(snapshot, endpoint.Id, DeviceSessionKind.Hdc,
                TestContext.Current.CancellationToken);
            if (!lease.TryGetSession<IHdcDeviceSession>(out var session))
            {
                await lease.DisposeAsync();
                throw new InvalidOperationException("The HDC lease did not carry a device session.");
            }

            if (!session.IsConnected)
            {
                await session.ConnectAsync(TestContext.Current.CancellationToken);
            }

            return (controller, lease, session);
        }
        catch
        {
            throw;
        }
    }

    [Fact]
    public async Task Discovery_ServerLive_SuppressesDirectUsbHdcEndpoints()
    {
        RequireTarget();
        var controller = DeviceControllerFactory.CreateDefault();
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var target = snapshot.Endpoints.FirstOrDefault(candidate =>
            candidate.Protocol == "hdc" && candidate.Address == Serial);
        Assert.NotNull(target);
        Assert.Equal("server", target!.Route);
        Assert.DoesNotContain(snapshot.Endpoints, candidate =>
            candidate.Route == "direct-usb" && candidate.Protocol == "hdc");
    }

    [Fact]
    public async Task ServerSession_ShellCommand_ReturnsOutput()
    {
        RequireTarget();
        var (controller, lease, session) = await LeaseDeviceAsync();
        try
        {
            var result = session.ExecuteCommand("echo hdc-suite-ok");
            Assert.Contains("hdc-suite-ok", System.Text.Encoding.UTF8.GetString(result.Stdout));
            Assert.True(result.ExitCode is 0 or -1, $"Unexpected exit code {result.ExitCode}.");
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task InteractiveShellStream_DeliversIncrementalOutput()
    {
        RequireTarget();
        var (controller, lease, session) = await LeaseDeviceAsync();
        try
        {
            var output = new MemoryStream();
            var shell = session.CreateShellClient("echo stream-one; sleep 1; echo stream-two");
            shell.ExecuteStreaming(
                chunk => { lock (output) output.Write(chunk); },
                chunk => { lock (output) output.Write(chunk); },
                TestContext.Current.CancellationToken);
            var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
            Assert.Contains("stream-one", text);
            Assert.Contains("stream-two", text);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeviceVariables_PopulateStaticCache()
    {
        RequireTarget();
        var (controller, lease, session) = await LeaseDeviceAsync();
        try
        {
            var model = System.Text.Encoding.UTF8.GetString(
                session.ExecuteCommand("param get const.product.model").Stdout).Trim();
            var brand = System.Text.Encoding.UTF8.GetString(
                session.ExecuteCommand("param get const.product.brand").Stdout).Trim();
            Assert.False(string.IsNullOrWhiteSpace(model));

            if (controller.Properties.Get(Serial) is null)
            {
                controller.Properties.Store(Serial, new Dictionary<string, string>
                {
                    ["model"] = model,
                    ["brand"] = brand
                });
            }

            Assert.False(string.IsNullOrWhiteSpace(controller.Properties.Get(Serial)!["model"]));
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reboot_DeviceReturnsToSystem()
    {
        RequireTarget();
        if (!string.Equals(Environment.GetEnvironmentVariable("UOTAN_TEST_ALLOW_REBOOT"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("UOTAN_TEST_ALLOW_REBOOT!=1; reboot scenarios stay opt-in.");
        }

        var controller = DeviceControllerFactory.CreateDefault();
        try
        {
            var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
            var endpoint = snapshot.Endpoints.First(candidate =>
                candidate.Protocol == "hdc" && candidate.Address == Serial);
            await using var lease = await controller.OpenAsync(snapshot, endpoint.Id,
                DeviceSessionKind.Hdc, TestContext.Current.CancellationToken);
            Assert.True(lease.TryGetSession<IHdcDeviceSession>(out var session));
            if (!session.IsConnected)
            {
                await session.ConnectAsync(TestContext.Current.CancellationToken);
            }

            session.CreateDeviceServices().Reboot();
        }
        finally
        {
        }

        var watcher = DeviceControllerFactory.CreateDefault();
        var deadline = Environment.TickCount64 + (int)TimeSpan.FromMinutes(3).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(3000, TestContext.Current.CancellationToken);
            try
            {
                var refreshed = await watcher.RefreshAsync(TestContext.Current.CancellationToken);
                if (refreshed.Endpoints.Any(candidate => candidate.Protocol == "hdc" && candidate.Address == Serial))
                {
                    return;
                }
            }
            catch { }
        }

        Assert.Fail("The HDC target did not return within the timeout after reboot.");
    }
}

/// <summary>
/// Environment-mutating HDC scenarios (server kill/restart, device reboot). Must not run in
/// the same invocation as <see cref="HdcRealDeviceSuite"/> because stopping the hdc server
/// disturbs the server-route tests.
/// <para>会变更环境的 HDC 场景（杀/重启 server、设备重启）。不得与 HdcRealDeviceSuite
/// 在同一次调用中运行，因为停止 hdc server 会干扰 server 路由测试。</para>
/// </summary>
public sealed class HdcDirectChannelSuite
{
    private static string Serial => Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_TARGET") ?? string.Empty;

    private static void RequireTarget()
    {
        if (string.IsNullOrWhiteSpace(Serial))
        {
            Assert.Skip("UOTAN_TEST_HDC_TARGET is not set; real-device suite is opt-in.");
        }
    }

    /// <summary>
    /// File sync requires a direct daemon channel (server host-channels do not carry the file
    /// protocol), so this test stops the hdc server, waits for the takeover to release the
    /// direct-usb endpoint, opens an authenticated direct session and round-trips a file.
    /// <para>文件 sync 需要直连 daemon 通道（server 主机通道不承载文件协议）：本测试先停掉
    /// hdc server，等待接管解除出现 direct-usb 端点，打开直连认证会话并做文件往返。</para>
    /// </summary>
    [Fact]
    public async Task FileTransfer_DirectChannel_RoundTrip_PreservesBytes()
    {
        RequireTarget();
        var controller = DeviceControllerFactory.CreateDefault();
        try
        {
            var killer = new UotanStudio.Comm.Protocols.Hdc.HdcServerClient();
            await killer.KillAsync(cancellationToken: TestContext.Current.CancellationToken);
            var directEndpoint = await WaitForEndpointAsync(controller, route: "direct-usb");
            var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
            var endpoint = snapshot.Endpoints.Single(candidate => candidate.Id == directEndpoint.Id);

            await using var lease = await controller.OpenAsync(snapshot, endpoint.Id,
                DeviceSessionKind.Hdc, TestContext.Current.CancellationToken);
            Assert.True(lease.TryGetSession<IHdcDeviceSession>(out var session));
            if (!session.IsConnected)
            {
                await session.ConnectAsync(TestContext.Current.CancellationToken);
            }

            var payload = new byte[1024 * 1024 + 777];
            new Random(20260925).NextBytes(payload);
            var local = Path.Combine(Path.GetTempPath(), $"hdc_suite_{Guid.NewGuid():N}.bin");
            var pulled = local + ".pull";
            try
            {
                await File.WriteAllBytesAsync(local, payload, TestContext.Current.CancellationToken);
                var remote = "/data/local/tmp/hdc_suite_roundtrip.bin";
                var sync = session.CreateSyncClient();
                sync.Send(local, remote, cancellationToken: TestContext.Current.CancellationToken);
                sync.Recv(remote, pulled, TestContext.Current.CancellationToken);
                var roundTrip = await File.ReadAllBytesAsync(pulled, TestContext.Current.CancellationToken);
                Assert.Equal(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(payload)),
                    Convert.ToHexString(System.Security.Cryptography.MD5.HashData(roundTrip)));
                session.ExecuteCommand($"rm -f {remote}");
            }
            finally
            {
                File.Delete(local);
                if (File.Exists(pulled)) File.Delete(pulled);
            }
        }
        finally
        {
            try
            {
                var restore = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("hdc", "list targets") { CreateNoWindow = true });
                restore?.WaitForExit(10000);
            }
            catch { }
        }
    }

    private static async Task<UotanStudio.Comm.Contracts.DeviceEndpoint> WaitForEndpointAsync(
        DeviceController controller, string route)
    {
        var deadline = Environment.TickCount64 + 20000;
        while (Environment.TickCount64 < deadline)
        {
            var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
            var endpoint = snapshot.Endpoints.FirstOrDefault(candidate =>
                candidate.Protocol == "hdc" && candidate.Route == route &&
                (route != "direct-usb" || candidate.Serial == Serial ||
                    string.IsNullOrEmpty(candidate.Serial)));
            if (endpoint is not null)
            {
                return endpoint;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No {route} HDC endpoint appeared for '{Serial}'.");
    }
}