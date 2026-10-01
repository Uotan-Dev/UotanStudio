using UotanStudio.Comm;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// <para>校验设备可被直连 USB 打开（server 权威守卫 + 唯一物理关联），返回用于打开稳定按键；
    /// 不可直连时返回 <c>null</c>。会话封装由控制器完成，宿主不再取得原始 USB 会话句柄。</para>
    /// Validates that the device may be opened over direct USB (server-authority guard + unique physical
    /// association) and returns the stable key to open it with, or <c>null</c> when not directly openable.
    /// The controller wraps the session; the host never receives a raw USB session handle.
    /// </summary>
    public string? ResolveDirectUsbOpenKey(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol == DeviceProtocolKind.Adb &&
            (device.Key.StartsWith("adb:", StringComparison.Ordinal) ||
                DebugBridgeSessionOpener.AdbServerOwns(device.Serial)))
            throw new IOException("ADB server owns this target; direct USB access is prohibited.");
        if (device.Protocol == DeviceProtocolKind.Hdc && DebugBridgeRoute.UseHdcServer())
            throw new IOException("HDC server owns this protocol; direct USB access is prohibited.");
        var direct = ResolveDirectUsbDevice(device);
        return direct.Transport == DeviceTransportKind.Usb ? direct.Key : null;
    }

    /// <summary>Opens a lease for the exact stable physical key via the controller; null when the key cannot open.</summary>
    internal DeviceLease? OpenLeaseByKey(string deviceKey, DeviceSessionKind kind)
        => _controller.OpenLeaseByKeyAsync(deviceKey, kind).GetAwaiter().GetResult();

    internal static DetectedDevice ResolveDirectUsbDevice(DetectedDevice device)
    {
        if (!device.Key.StartsWith("adb:", StringComparison.Ordinal) &&
            !device.Key.StartsWith("hdc:", StringComparison.Ordinal))
        {
            return device;
        }
        var matches = App.DeviceMonitor.ListUsbDevices()
            .Where(info => Classify(info) == device.Protocol &&
                string.Equals(info.SerialNumber, device.Serial, StringComparison.Ordinal)).ToArray();
        var match = RequireUniqueUsbIdentity(matches);
        return new DetectedDevice
        {
            Key = match.DeviceKey,
            Serial = device.Serial,
            Protocol = device.Protocol,
            Transport = DeviceTransportKind.Usb,
            State = "device",
            VendorId = match.VendorId,
            ProductId = match.ProductId
        };
    }

    internal static UsbDeviceInfo RequireUniqueUsbIdentity(IReadOnlyList<UsbDeviceInfo> matches)
    {
        if (matches.Count != 1)
        {
            throw new IOException("The server target cannot be uniquely matched to a physical USB device. Refresh the device list and select the USB target.");
        }
        return matches[0];
    }

    /// <summary>
    /// <para>为设备打开 ADB 会话租约：server 目标与被 server 拥有的序列号改经 server 会话服务（绝不直写 USB），
    /// 直连目标按稳定键租约化 USB；server 不存在且 USB 无法打开时返回 <c>null</c>。调用方持有租约并负责释放。</para>
    /// Opens an ADB session lease for the device: server targets and server-owned serials are served through
    /// the server (never raw USB), direct targets lease the exact USB key; returns <c>null</c> when no server
    /// exists and the USB device cannot be opened. Callers own the lease and must dispose it.
    /// </summary>
    public (DeviceLease Lease, IAdbDeviceSession Session)? OpenAdbLease(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb) return null;
        if (device.Key.StartsWith("adb:", StringComparison.Ordinal))
        {
            if (!DebugBridgeRoute.UseAdbServer())
                throw new IOException("The selected ADB server target is unavailable. Refresh and select the USB target for direct access.");
            return OpenServerAdbLease(device.Serial);
        }
        if (device.Transport == DeviceTransportKind.Tcp && !DebugBridgeRoute.UseAdbServer())
            throw new IOException("A network ADB target requires the ADB server.");
        if (DebugBridgeSessionOpener.AdbServerOwns(device.Serial))
        {
            return OpenServerAdbLease(device.Serial);
        }

        var key = ResolveDirectUsbOpenKey(device);
        if (key is null) return null;
        var lease = _controller.OpenLeaseByKeyAsync(key, DeviceSessionKind.Adb).GetAwaiter().GetResult();
        if (lease is null) return null;
        if (!lease.TryGetSession<IAdbDeviceSession>(out var session))
        {
            CloseLease(lease);
            throw new IOException("The selected USB endpoint does not expose an ADB session.");
        }
        return (lease, session);
    }

    private (DeviceLease Lease, IAdbDeviceSession Session) OpenServerAdbLease(string serial)
    {
        var session = _controller.OpenServerAdbSession(serial,
            DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort);
        var endpoint = new DeviceEndpoint
        {
            Source = $"adb-server@{DeviceCommunicationConfiguration.ServerHost}:{DeviceCommunicationConfiguration.AdbServerPort}",
            Transport = "adb-server",
            Address = serial,
            DisplayName = serial,
            Serial = serial,
            Route = "server",
            Protocol = "adb"
        };
        return (_controller.AdoptSession(endpoint, session), session);
    }

    /// <summary>
    /// <para>判断 HDC 目标是否应经本机 hdc server 交互；非 USB 目标无 server 时拒绝。</para>
    /// Decides whether an HDC target must go through the local hdc server; non-USB targets are
    /// rejected when no server is present.
    /// </summary>
    internal static bool UseHdcServerFor(DetectedDevice device, string host = DeviceCommunicationConfiguration.ServerHost, int? port = null)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc || string.IsNullOrWhiteSpace(device.Serial))
            throw new ArgumentException("An HDC target with a connect key is required.", nameof(device));
        if (DebugBridgeRoute.UseHdcServer(host, port)) return true;
        if (device.Transport != DeviceTransportKind.Usb)
            throw new IOException("A non-USB HDC target requires the HDC server.");
        return false;
    }

    /// <summary>
    /// <para>为设备打开 HDC 会话租约：server 在时经 server 会话服务，不在时按稳定键直连 USB；
    /// 非 HDC 协议返回 <c>null</c>。调用方持有租约并负责释放。</para>
    /// Opens an HDC session lease for the device: through the server when one is running, otherwise
    /// the exact USB key; returns <c>null</c> for non-HDC devices. Callers own the lease and must dispose it.
    /// </summary>
    public (DeviceLease Lease, HdcSession Session)? OpenHdcLease(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc) return null;
        _ = UseHdcServerFor(device);
        if (DebugBridgeRoute.UseHdcServer())
        {
            var port = HdcServerClient.DefaultServerPort;
            using var server = new HdcServerClient(DeviceCommunicationConfiguration.ServerHost, port);
            var session = server.CreateSession(device.Serial);
            var endpoint = new DeviceEndpoint
            {
                Source = $"hdc-server@{DeviceCommunicationConfiguration.ServerHost}:{port}",
                Transport = "hdc-server",
                Address = device.Serial,
                DisplayName = device.Serial,
                Serial = device.Serial,
                Route = "server",
                Protocol = "hdc"
            };
            return (_controller.AdoptSession(endpoint, session), session);
        }

        return HdcDirectConnection.Open(device);
    }

    /// <summary>Executes a shell command against the exact selected ADB server target.</summary>
    public string ExecuteAdbServerShell(DetectedDevice device, string command)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("An ADB target is required.", nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!DebugBridgeRoute.UseAdbServer())
            throw new IOException("The ADB server is unavailable.");
        EnsureAdbServerTarget(device.Serial);
        return new AdbServerServices(device.Serial,
            DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort)
            .RunShellAsync(command).GetAwaiter().GetResult();
    }

    /// <summary>Checks whether an official adb server is running and reachable.</summary>
    public bool HasAdbServer => DebugBridgeRoute.UseAdbServer();

    /// <summary>Requires the exact server-owned ADB target without opening a direct USB session.</summary>
    public void RequireAdbServerDevice(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("An ADB target is required.", nameof(device));
        if (!DebugBridgeRoute.UseAdbServer())
            throw new IOException("The ADB server is unavailable.");
        EnsureAdbServerTarget(device.Serial);
    }

    /// <summary>Probes the selected ADB target through the controller lease; server errors never grant direct USB access.</summary>
    public bool ProbeAdb(DetectedDevice device)
    {
        try { return RunAdb(device, session => session.IsConnected); }
        catch { return false; }
    }

    /// <summary>Probes the selected Fastboot target and reads its product variable.</summary>
    public (bool Ready, string? Product) ProbeFastboot(DetectedDevice device)
    {
        try { return (true, RunFastboot(device, session => session.GetVariable("product", useCache: false))); }
        catch { return (false, null); }
    }

    /// <summary>Checks whether the selected RKDL target is responsive.</summary>
    public bool ProbeRkdl(DetectedDevice device)
    {
        try
        {
            return RunRkdlAsync(device, session =>
                Task.FromResult(session.IsRockchipDevice() && session.TestDeviceReady())).GetAwaiter().GetResult();
        }
        catch { return false; }
    }

    /// <summary>Lists the targets managed by the running adb server, or an empty list when it is absent.</summary>
    internal static IReadOnlyList<(string Serial, string State)> ListAdbServerTargets() =>
        new AdbServerServices(string.Empty, DeviceCommunicationConfiguration.ServerHost,
            DeviceCommunicationConfiguration.AdbServerPort).ListTargets();

    private static void EnsureAdbServerTarget(string serial)
    {
        if (!ListAdbServerTargets().Any(target => string.Equals(target.Serial, serial, StringComparison.Ordinal)))
            throw new IOException("The ADB server does not contain the selected target. Direct USB fallback is prohibited while the server is running.");
    }

    /// <summary>Reports whether the running server manages the device, for routing server versus direct-USB work.</summary>
    private static bool AdbServerManages(DetectedDevice device)
    {
        if (!DebugBridgeRoute.UseAdbServer())
        {
            return false;
        }

        var present = ListAdbServerTargets().Any(target =>
            string.Equals(target.Serial, device.Serial, StringComparison.Ordinal));
        if (!present && device.Key.StartsWith("adb:", StringComparison.Ordinal))
            throw new IOException("The selected ADB server target is no longer present.");
        return present;
    }

    /// <summary>Checks the current HDC server and propagates handshake errors.</summary>
    private HdcServerClient? ProbeHdcServer()
    {
        if (!DebugBridgeRoute.UseHdcServer()) return null;
        var client = new HdcServerClient();
        try { client.Connect(); return client; }
        catch { client.Dispose(); throw; }
    }

}