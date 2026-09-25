using UotanStudio.Mcp.Tools;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Mcp;
using UotanStudio.Services.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>Opt-in read-only target checks; scrcpy additionally deploys its temporary server.</summary>
[Collection("Device manager hardware")]
public sealed class DeviceManagerHardwareTests
{
    [Fact]
    public void FastbootTarget_HeadlessMcpManager_ReadsSelectedSerial()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_FASTBOOT_SERIAL");
        if (string.IsNullOrWhiteSpace(serial))
            Assert.Skip("Set UOTAN_TEST_FASTBOOT_SERIAL to the only connected Fastboot target.");

        var manager = new HeadlessDeviceManagerGateway();
        var reported = manager.RunFastboot(session => session.GetVariable("serialno", useCache: false));

        Assert.Equal(serial, reported);
    }

    [Fact]
    public void HdcUsbTarget_ServerPresent_ManagerScan_DoesNotPublishDirectEndpoint()
    {
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_USB_ID");
        if (string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_HDC_USB_ID to the VID:PID of a present HDC USB interface.");
        if (!UotanStudio.Comm.Routing.DebugBridgeRoute.UseHdcServer())
            Assert.Skip("The HDC server must be running for this routing check.");
        var monitor = UotanStudio.App.DeviceMonitor;
        Assert.Contains(monitor.ListUsbDevices(), info =>
            $"{info.VendorId:X4}:{info.ProductId:X4}" == identity);
        Assert.DoesNotContain(monitor.Scan(), device =>
            device.Protocol == DeviceProtocolKind.Hdc &&
            $"{device.VendorId:X4}:{device.ProductId:X4}" == identity);
    }

    [Fact]
    public void DebugBridgeServersPresent_McpManagers_ExcludeOwnedUsbInterfaces()
    {
        var adbId = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        var hdcId = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_USB_ID");
        if (string.IsNullOrWhiteSpace(adbId) || string.IsNullOrWhiteSpace(hdcId))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID and UOTAN_TEST_HDC_USB_ID to present USB interfaces.");
        if (!UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer() ||
            !UotanStudio.Comm.Routing.DebugBridgeRoute.UseHdcServer())
            Assert.Skip("Both debug bridge servers must be running.");

        static string Identity(UotanStudio.Comm.Transports.Usb.Abstractions.UsbDeviceInfo info)
            => $"{info.VendorId:X4}:{info.ProductId:X4}";
        var raw = UotanStudio.App.DeviceMonitor.ListUsbDevices();
        Assert.Contains(raw, info => Identity(info) == adbId);
        Assert.Contains(raw, info => Identity(info) == hdcId);
        foreach (var gateway in new IDeviceManagerGateway[]
        {
            new DesktopDeviceManagerGateway(), new HeadlessDeviceManagerGateway()
        })
        {
            var routable = gateway.ListUsbDevices();
            Assert.DoesNotContain(routable, info => Identity(info) == adbId);
            Assert.DoesNotContain(routable, info => Identity(info) == hdcId);
        }
    }

    [Fact]
    public void UsbTargets_WindowsContainerIdentity_MatchesPnp()
    {
        var adbContainer = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_CONTAINER");
        var hdcContainer = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_CONTAINER");
        var hasAdbId = Guid.TryParse(adbContainer, out var adbId);
        var hasHdcId = Guid.TryParse(hdcContainer, out var hdcId);
        if (!hasAdbId || !hasHdcId)
            Assert.Skip("Set UOTAN_TEST_ADB_CONTAINER and UOTAN_TEST_HDC_CONTAINER to current PnP ContainerIDs.");
        var infos = UotanStudio.App.DeviceMonitor.ListUsbDevices();
        Assert.Equal(adbId, Assert.Single(infos, info =>
            info.VendorId == 0x22D9 && info.ProductId == 0x2769).ContainerId);
        Assert.Equal(hdcId, Assert.Single(infos, info =>
            info.VendorId == 0x12D1 && info.ProductId == 0x5000).ContainerId);
    }

    [Fact]
    public async Task AdbUsbTarget_ServerAbsent_ManagerDirectShell_RemainsReadable()
    {
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        if (string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID to the VID:PID of a present ADB interface with the server stopped.");
        if (UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer())
            Assert.Skip("Stop the ADB server before running the direct USB check.");

        var monitor = UotanStudio.App.DeviceMonitor;
        var device = Assert.Single(monitor.Scan(), candidate =>
            candidate.Protocol == DeviceProtocolKind.Adb &&
            $"{candidate.VendorId:X4}:{candidate.ProductId:X4}" == identity);
        Assert.Equal("device", await monitor.GetAdbStateAsync(device.Serial, TestContext.Current.CancellationToken));
        Assert.True(monitor.ProbeAdb(device));
        var (output, stat, entries, backend) = monitor.RunAdb(device, session =>
        {
            var shell = session.ExecuteCommand("getprop ro.product.model");
            using var sync = session.CreateSyncClient();
            var file = sync.Stat("/system/build.prop");
            var directory = sync.List("/system");
            var api = ((UotanStudio.Comm.Transports.Abstractions.UsbDeviceTransport)((UotanStudio.Comm.Protocols.Adb.AdbSession)session).Transport)
                .Session.DeviceInfo.SourceApiKind;
            return (shell, file, directory, api);
        },
            TestContext.Current.CancellationToken);
        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(stat);
        Assert.Contains(entries, entry => entry.Name == "build.prop");
        if (OperatingSystem.IsWindows())
            Assert.Equal(UotanStudio.Comm.Transports.Usb.Abstractions.UsbApiKind.Native, backend);
    }

    [Fact]
    public void AdbUsbTarget_ServerAbsent_LibUsbShell_RemainsReadable()
    {
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        if (string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID to a connected ADB USB interface.");
        if (UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer())
            Assert.Skip("Stop the ADB server before testing direct libusb ownership.");
        var nativeLibrary = Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dll");
        if (!File.Exists(nativeLibrary))
            Assert.Skip("The libusb runtime is not available in this test output.");
        UotanStudio.Comm.CommKit.SetLibusbLibraryPath(nativeLibrary);
        var kit = new UotanStudio.Comm.CommKit();
        var parts = identity.Split(':');
        var filter = new UotanStudio.Comm.Transports.Usb.Abstractions.UsbDeviceFilter
        {
            VendorId = Convert.ToUInt16(parts[0], 16),
            ProductId = Convert.ToUInt16(parts[1], 16),
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
            InterfaceProtocol = 0x01
        };
        var api = UotanStudio.Comm.Transports.Usb.Abstractions.UsbApiKind.LibUsbDotNet;
        var info = Assert.Single(kit.EnumerateUsbDevices(api, filter));
        var usb = kit.OpenUsbDeviceSessionByKey(info.DeviceKey, api);
        Assert.NotNull(usb);
        using var session = new UotanStudio.Comm.Protocols.Adb.AdbSession(
            new UotanStudio.Comm.Transports.Abstractions.UsbDeviceTransport(usb));
        session.Connect();
        var model = session.ExecuteCommand("getprop ro.product.model");
        Assert.Equal(0, model.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(System.Text.Encoding.UTF8.GetString(model.Stdout)));
    }

    [Fact]
    public void AdbUsbTarget_ServerAbsent_LogcatSnapshotStreamsBytes()
    {
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        if (string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID to a connected ADB USB interface.");
        if (UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer())
            Assert.Skip("Stop the ADB server before testing the direct shell v2 stream.");
        var monitor = UotanStudio.App.DeviceMonitor;
        var device = Assert.Single(monitor.Scan(), candidate =>
            candidate.Protocol == DeviceProtocolKind.Adb &&
            $"{candidate.VendorId:X4}:{candidate.ProductId:X4}" == identity);
        var bytes = monitor.RunAdb(device, session =>
        {
            var count = 0;
            var exitCode = session.CreateShellClient("logcat -d -t 5 -v year").ExecuteStreaming(
                chunk => count += chunk.Length, null, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            return count;
        }, TestContext.Current.CancellationToken);

        Assert.True(bytes > 0, "The direct shell v2 logcat snapshot produced no stdout.");
    }

    [Fact]
    public async Task AdbUsbTarget_ServerManagesOtherTarget_ConcurrentRoutesRemainReadable()
    {
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        var serverSerial = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_SERVER_TARGET");
        if (string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID to an attached ADB interface for read-only validation.");
        if (string.IsNullOrWhiteSpace(serverSerial))
            Assert.Skip("Set UOTAN_TEST_ADB_SERVER_TARGET to a different target managed by the server.");
        if (!UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer())
            Assert.Skip("An ADB server must be running for this mixed-route check.");

        var monitor = UotanStudio.App.DeviceMonitor;
        var usbInfos = monitor.ListUsbDevices();
        var selectedUsb = Assert.Single(usbInfos, info =>
            $"{info.VendorId:X4}:{info.ProductId:X4}" == identity &&
            info.InterfaceClass == 0xFF && info.InterfaceSubClass == 0x42 && info.InterfaceProtocol == 0x01);
        var targets = monitor.Scan();
        var serverDevice = Assert.Single(targets, candidate =>
            candidate.Protocol == DeviceProtocolKind.Adb &&
            candidate.Key.StartsWith("adb:", StringComparison.Ordinal) &&
            candidate.Serial == serverSerial);
        var directCandidates = targets.Where(candidate =>
            candidate.Protocol == DeviceProtocolKind.Adb &&
            candidate.Transport == DeviceTransportKind.Usb &&
            !candidate.Key.StartsWith("adb:", StringComparison.Ordinal) &&
            $"{candidate.VendorId:X4}:{candidate.ProductId:X4}" == identity).ToArray();
        Assert.True(directCandidates.Length == 1,
            $"USB serial={selectedUsb.SerialNumber ?? "<null>"}, key={selectedUsb.DeviceKey}; " +
            $"server targets={string.Join(", ", targets.Where(target => target.Key.StartsWith("adb:", StringComparison.Ordinal)).Select(target => target.Serial))}");
        var device = directCandidates[0];
        var token = TestContext.Current.CancellationToken;
        var direct = Task.Run(() => monitor.RunAdb(device, session =>
        {
            Assert.False(session.UsesServer);
            var result = session.ExecuteCommand("getprop ro.product.model");
            Assert.Equal(0, result.ExitCode);
            return System.Text.Encoding.UTF8.GetString(result.Stdout);
        }, token), token);
        var server = Task.Run(() => monitor.RunAdb(serverDevice, session =>
        {
            Assert.True(session.UsesServer);
            var result = session.ExecuteCommand("getprop ro.product.model");
            Assert.Equal(0, result.ExitCode);
            return System.Text.Encoding.UTF8.GetString(result.Stdout);
        }, token), token);
        var models = await Task.WhenAll(direct, server);
        Assert.All(models, model => Assert.False(string.IsNullOrWhiteSpace(model)));
    }

    [Fact]
    public void AdbUsbTarget_ServerAbsent_EmbeddedMcpGateway_RoutesDirectShell()
    {
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        if (string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID to the VID:PID of a present ADB interface with the server stopped.");
        if (UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer())
            Assert.Skip("Stop the ADB server before running the direct USB check.");

        var gateway = new DesktopDeviceManagerGateway();
        var target = Assert.Single(gateway.ListAdbTargets(), candidate => candidate.State == "device");
        var output = gateway.RunAdbService(target.Serial, "shell:getprop ro.product.model", stream =>
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            return reader.ReadToEnd();
        });
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public void AdbUsbTarget_ServerAbsent_HeadlessMcpGateway_RoutesDirectShell()
    {
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        if (string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID to the VID:PID of a present ADB interface with the server stopped.");
        if (UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer())
            Assert.Skip("Stop the ADB server before running the direct USB check.");

        var gateway = new HeadlessDeviceManagerGateway();
        var target = Assert.Single(gateway.ListAdbTargets(), candidate => candidate.State == "device");
        var output = gateway.RunAdbService(target.Serial, "shell:getprop ro.product.model", stream =>
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            return reader.ReadToEnd();
        });
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public async Task HdcUsbTarget_ServerAbsent_ManagerDirectShell_RemainsReadable()
    {
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_USB_ID");
        if (string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_HDC_USB_ID to the VID:PID of a present HDC interface with the server stopped.");
        if (UotanStudio.Comm.Routing.DebugBridgeRoute.UseHdcServer())
            Assert.Skip("Stop the HDC server before running the direct USB check.");

        var monitor = UotanStudio.App.DeviceMonitor;
        var device = Assert.Single(monitor.Scan(), candidate =>
            candidate.Protocol == DeviceProtocolKind.Hdc &&
            $"{candidate.VendorId:X4}:{candidate.ProductId:X4}" == identity);
        Assert.Equal("ready", (await monitor.HdcShellAsync(device, "echo ready",
            TestContext.Current.CancellationToken)).Trim());
    }

    [Fact]
    public async Task FastbootTarget_ManagerProbeAndPartitionVariables_RemainReadable()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_FASTBOOT_SERIAL");
        if (string.IsNullOrWhiteSpace(serial))
            Assert.Skip("Set UOTAN_TEST_FASTBOOT_SERIAL to a currently connected Fastboot target.");
        var monitor = UotanStudio.App.DeviceMonitor;
        var device = Assert.Single(monitor.Scan(), target =>
            target.Protocol == DeviceProtocolKind.Fastboot && target.Serial == serial);
        var reportedSerial = monitor.RunFastboot(device, session => session.GetVariable("serialno", useCache: false),
            TestContext.Current.CancellationToken);
        Assert.Equal(serial, reportedSerial);

        var (ready, product) = monitor.ProbeFastboot(device);
        Assert.True(ready);
        Assert.False(string.IsNullOrWhiteSpace(product));
        var partitions = await FastbootPartitionReader.ReadAsync(device, showAll: true,
            TestContext.Current.CancellationToken);
        Assert.All(partitions, partition => Assert.True(partition.Size > 0));
    }

    [Fact]
    public async Task AdbTarget_ManagerProbeAndShell_RemainReadable()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_TARGET");
        if (string.IsNullOrWhiteSpace(serial))
            Assert.Skip("Set UOTAN_TEST_ADB_TARGET to a currently connected ADB target.");
        var monitor = UotanStudio.App.DeviceMonitor;
        var device = Assert.Single(monitor.Scan(), target =>
            target.Protocol == DeviceProtocolKind.Adb && target.Serial == serial);

        Assert.Equal("device", await monitor.GetAdbStateAsync(serial, TestContext.Current.CancellationToken));
        Assert.True(monitor.ProbeAdb(device));
        Assert.False(string.IsNullOrWhiteSpace(monitor.ExecuteAdbServerShell(device, "getprop ro.product.model")));
        var (stat, entries) = monitor.RunAdb(device, session =>
        {
            using var sync = session.CreateSyncClient();
            return (sync.Stat("/system/build.prop"), sync.List("/system"));
        }, TestContext.Current.CancellationToken);
        Assert.NotNull(stat);
        Assert.Contains(entries, entry => entry.Name == "build.prop");
        Assert.True(UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer());
        Assert.Contains(new UotanStudio.Comm.Protocols.Adb.AdbServerServices(string.Empty).ListTargets(),
            target => target.Serial == serial);
        foreach (var gateway in new IDeviceManagerGateway[]
        {
            new DesktopDeviceManagerGateway(), new HeadlessDeviceManagerGateway()
        })
        {
            var targets = gateway.ListAdbTargets();
            Assert.True(targets.Any(target => target.Serial == serial && target.State is "device" or "online"),
                $"{gateway.GetType().Name}: {string.Join(", ", targets.Select(target => $"{target.Serial}:{target.State}"))}");
            var model = gateway.RunAdbService(serial, "shell:getprop ro.product.model", stream =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                return reader.ReadToEnd();
            });
            Assert.False(string.IsNullOrWhiteSpace(model));
        }
    }

    [Fact]
    public void AdbTarget_UsbIdentity_RequiresUniquePhysicalAssociation()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_TARGET");
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_TARGET and UOTAN_TEST_ADB_USB_ID for physical identity validation.");

        var infos = UotanStudio.App.DeviceMonitor.ListUsbDevices();
        var usb = Assert.Single(infos, info =>
            $"{info.VendorId:X4}:{info.ProductId:X4}" == identity &&
            info.InterfaceClass == 0xFF && info.InterfaceSubClass == 0x42 && info.InterfaceProtocol == 0x01);
        var associated = DeviceMonitorService.FindServerUsbIdentity(infos, serial, DeviceProtocolKind.Adb);
        Assert.True(associated is not null,
            $"No unique ADB-to-USB identity: USB serial={usb.SerialNumber ?? "<missing>"}, " +
            $"instance={usb.DeviceInstanceId ?? "<missing>"}, container={usb.ContainerId?.ToString() ?? "<missing>"}.");
        Assert.Equal(DeviceMonitorService.PhysicalUsbId(usb, usb.SerialNumber), associated);
    }

    [Fact]
    public void AdbTarget_ServerOwnsUsb_DirectOpenIsRejected()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_TARGET");
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_TARGET and UOTAN_TEST_ADB_USB_ID to the same server-managed device.");
        var monitor = UotanStudio.App.DeviceMonitor;
        Assert.True(UotanStudio.Comm.Devices.Services.DebugBridgeSessionOpener.AdbServerOwns(serial));
        var info = Assert.Single(monitor.ListUsbDevices(), usb =>
            $"{usb.VendorId:X4}:{usb.ProductId:X4}" == identity &&
            usb.InterfaceClass == 0xFF && usb.InterfaceSubClass == 0x42 && usb.InterfaceProtocol == 0x01);
        var direct = new DetectedDevice
        {
            Key = info.DeviceKey,
            Serial = serial,
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb
        };

        Assert.Throws<IOException>(() => monitor.ResolveDirectUsbOpenKey(direct));
    }

    [Fact]
    public async Task AdbTarget_InteractiveShell_RoutesSelectedDeviceAndExits()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_TARGET");
        if (string.IsNullOrWhiteSpace(serial))
            Assert.Skip("Set UOTAN_TEST_ADB_TARGET to a connected ADB target for read-only shell validation.");
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        var server = UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer();
        if (!server && string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID to the direct USB interface when the server is absent.");
        var device = Assert.Single(UotanStudio.App.DeviceMonitor.Scan(), target =>
            target.Protocol == DeviceProtocolKind.Adb &&
            (server ? target.Serial == serial :
                $"{target.VendorId:X4}:{target.ProductId:X4}" == identity));
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("echo UOTAN_READONLY; exit\n"));
        using var output = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        await UotanStudio.App.DeviceMonitor.RunInteractiveShellAsync(device, input, output, timeout.Token);

        Assert.Contains("UOTAN_READONLY", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task AdbTarget_LogcatStream_ReadsAndCancelsOnSelectedRoute()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_TARGET");
        var identity = Environment.GetEnvironmentVariable("UOTAN_TEST_ADB_USB_ID");
        if (string.IsNullOrWhiteSpace(serial))
            Assert.Skip("Set UOTAN_TEST_ADB_TARGET to a connected ADB target for read-only log validation.");
        var server = UotanStudio.Comm.Routing.DebugBridgeRoute.UseAdbServer();
        if (!server && string.IsNullOrWhiteSpace(identity))
            Assert.Skip("Set UOTAN_TEST_ADB_USB_ID for direct USB log validation.");
        var device = Assert.Single(UotanStudio.App.DeviceMonitor.Scan(), target =>
            target.Protocol == DeviceProtocolKind.Adb &&
            (server ? target.Serial == serial :
                $"{target.VendorId:X4}:{target.ProductId:X4}" == identity));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(35));
        var count = 0;
        try
        {
            await UotanStudio.App.DeviceMonitor.StartLogcatStreamingAsync(device,
                _ =>
                {
                    if (Interlocked.Increment(ref count) >= 3) cancellation.Cancel();
                }, cancellation.Token).WaitAsync(
                    TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

        Assert.True(count > 0, "The selected ADB target produced no logcat records.");
    }

    [Fact]
    public async Task HdcTarget_ManagerShellAndProperty_RemainReadable()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_TARGET");
        if (string.IsNullOrWhiteSpace(serial))
            Assert.Skip("Set UOTAN_TEST_HDC_TARGET to a currently connected HDC target.");
        var monitor = UotanStudio.App.DeviceMonitor;
        var device = Assert.Single(monitor.Scan(), target =>
            target.Protocol == DeviceProtocolKind.Hdc && target.Serial == serial);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("ready", (await monitor.HdcShellAsync(device, "echo ready", ct)).Trim());
        Assert.False(string.IsNullOrWhiteSpace(await monitor.HdcPropertyAsync(device, "const.product.model", ct)));
    }

    [Fact]
    public async Task AdbTarget_MirrorStartsReceivesVideoAndReleasesConnection()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_SCRCPY_TARGET");
        var jar = Environment.GetEnvironmentVariable("UOTAN_TEST_SCRCPY_JAR");
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(jar))
            Assert.Skip("Set UOTAN_TEST_SCRCPY_TARGET and UOTAN_TEST_SCRCPY_JAR to opt in to temporary server deployment.");
        var monitor = UotanStudio.App.DeviceMonitor;
        var device = Assert.Single(monitor.Scan(), target =>
            target.Protocol == DeviceProtocolKind.Adb && target.Serial == serial);
        Assert.True(File.Exists(jar));
        var ct = TestContext.Current.CancellationToken;
        var mirror = await Task.Run(() => monitor.StartMirror(device, jar, new ScrcpyServerOptions
        {
            Audio = false,
            Control = false,
            StayAwake = false,
            MaxSize = 720,
            MaxFps = 5,
            VideoBitRate = 1_000_000
        }), ct);
        try
        {
            await Task.Run(mirror.Connect, ct);
            Assert.NotNull(mirror.VideoMetadata);
            var frame = await Task.Run(() => mirror.ReadFrame(3000), ct);
            Assert.NotNull(frame);
            Assert.NotEmpty(frame.Data);
        }
        finally
        {
            mirror.Dispose();
        }
        Assert.True(monitor.ProbeAdb(device));
    }
}

[CollectionDefinition("Device manager hardware", DisableParallelization = true)]
public sealed class DeviceManagerHardwareCollection;