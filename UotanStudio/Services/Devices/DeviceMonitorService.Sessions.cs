using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Protocols.Scrcpy;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Flash;


namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// <para>为设备打开 USB 设备会话（按键重开，支持模式切换后 VID/PID 变化）。</para>
    /// Opens the USB device session for the device (reopen by key, tolerating VID/PID
    /// changes across mode switches).
    /// </summary>
    public IUsbDeviceSession? OpenUsbSession(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol == DeviceProtocolKind.Adb &&
            (device.Key.StartsWith("adb:", StringComparison.Ordinal) ||
                DebugBridgeRoute.AdbServerOwns(device.Serial)))
            throw new IOException("ADB server owns this target; direct USB access is prohibited.");
        if (device.Protocol == DeviceProtocolKind.Hdc && DebugBridgeRoute.UseHdcServer())
            throw new IOException("HDC server owns this protocol; direct USB access is prohibited.");
        var direct = ResolveDirectUsbDevice(device);
        return direct.Transport == DeviceTransportKind.Usb
            ? _comm.OpenUsbDeviceSessionByKey(direct.Key) : null;
    }

    internal static DetectedDevice ResolveDirectUsbDevice(DetectedDevice device)
    {
        if (!device.Key.StartsWith("adb:", StringComparison.Ordinal) &&
            !device.Key.StartsWith("hdc:", StringComparison.Ordinal))
        {
            return device;
        }
        var matches = CommKit.Default.EnumerateUsbDevices()
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

    /// <summary>Opens a server service session when a server exists, otherwise the exact USB device.</summary>
    public AdbSession? OpenAdbSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb) return null;
        if (device.Key.StartsWith("adb:", StringComparison.Ordinal))
        {
            if (!DebugBridgeRoute.UseAdbServer())
                throw new IOException("The selected ADB server target is unavailable. Refresh and select the USB target for direct access.");
            return new AdbSession(new AdbServerServices(device.Serial,
                DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort));
        }
        if (device.Transport == DeviceTransportKind.Tcp && !DebugBridgeRoute.UseAdbServer())
            throw new IOException("A network ADB target requires the ADB server.");
        return DebugBridgeRoute.OpenAdb(device.Serial, () =>
        {
            var usb = OpenUsbSession(device);
            if (usb is null) return null; // server 不存在且 USB 设备无法打开时视为不可用，而非抛异常
            try { return new AdbSession(new UsbDeviceTransport(usb)); }
            catch { usb.Dispose(); throw; }
        });
    }

    /// <summary>
    /// <para>为设备打开 Fastboot 协议会话。</para>
    /// Opens a Fastboot protocol session for the device.
    /// </summary>
    public FastbootSession? OpenFastbootSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
            throw new ArgumentException("A Fastboot target is required.", nameof(device));

        var usb = OpenUsbSession(device);
        if (usb is null) return null;
        var factory = new FastbootFactory();
        if (!factory.TryCreate(usb, out var protocolSession) || protocolSession is not FastbootSession session)
        {
            usb.Dispose();
            throw new InvalidOperationException("The device does not expose a fastboot interface.");
        }

        if (!session.IsConnected)
        {
            session.Dispose();
            throw new InvalidOperationException("Fastboot session is not connected.");
        }
        return session;
    }

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
    /// <para>为设备打开 EDL 协议会话。</para>
    /// Opens an EDL protocol session for the device.
    /// </summary>
    public EdlSession? OpenEdlSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Edl)
            throw new ArgumentException("An EDL target is required.", nameof(device));
        var usb = OpenUsbSession(device);
        return usb is null ? null : new EdlSession(new UsbDeviceTransport(usb));
    }

    /// <summary>
    /// <para>为设备打开 RKDL 协议会话。</para>
    /// Opens an RKDL protocol session for the device.
    /// </summary>
    public RkdlSession? OpenRkdlSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Rkdl)
            throw new ArgumentException("An RKDL target is required.", nameof(device));
        var usb = OpenUsbSession(device);
        return usb is null ? null : new RkdlSession(new UsbDeviceTransport(usb));
    }

    /// <summary>
    /// <para>为设备打开 HDC 协议会话：系统内存在 hdc server 时作为 client 经 server 交互
    /// （与 ADB server 模式一致），否则回退 USB 直连通信。</para>
    /// Opens an HDC protocol session for the device: when a local hdc server is present the
    /// toolbox talks to the device as a server client (mirroring the ADB server mode),
    /// otherwise it falls back to direct USB communication.
    /// </summary>
    public HdcSession? OpenHdcSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc) return null;
        _ = UseHdcServerFor(device);
        // A host channel is required even for USB targets managed by the official server.
        // Do not feed host frames into the legacy direct-device session transport.
        return DebugBridgeRoute.OpenHdc(device.Serial, () => HdcDirectSessionPool.Acquire(device));
    }

    /// <summary>
    /// <para>获取与 Google 官方 adb server 交互的客户端；server 未运行时返回 <c>null</c>。</para>
    /// Gets a client for the official Google adb server, or <c>null</c> when it is not running.
    /// </summary>
    private AdbClient? AdbServerClient => ProbeAdbServer();

    /// <summary>Executes a shell command against the exact selected ADB server target.</summary>
    public string ExecuteAdbServerShell(DetectedDevice device, string command)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("An ADB target is required.", nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var client = AdbServerClient ?? throw new IOException("The ADB server is unavailable.");
        var target = RequireAdbServerTarget(client, device.Serial);
        var receiver = new ConsoleOutputReceiver();
        client.ExecuteRemoteCommand(command, target, receiver);
        return receiver.ToString() ?? string.Empty;
    }

    /// <summary>Checks whether the ADB server can manage the selected target.</summary>
    public bool HasAdbServer => AdbServerClient is not null;

    /// <summary>Requires the exact server-owned ADB target without opening a direct USB session.</summary>
    public void RequireAdbServerDevice(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("An ADB target is required.", nameof(device));
        var client = AdbServerClient ?? throw new IOException("The ADB server is unavailable.");
        _ = RequireAdbServerTarget(client, device.Serial);
    }

    /// <summary>Probes the selected ADB target, releasing the session after the check.</summary>
    public bool ProbeAdb(DetectedDevice device)
    {
        try
        {
            using var session = OpenAdbSession(device);
            if (session is null) return false;
            session.Connect();
            return session.Connection.IsConnected;
        }
        catch { return false; }
    }

    /// <summary>Probes the selected Fastboot target and reads its product variable.</summary>
    public (bool Ready, string? Product) ProbeFastboot(DetectedDevice device)
    {
        using var session = OpenFastbootSession(device);
        if (session is null) return (false, null);
        try { return (true, session.GetVariable("product", useCache: false)); }
        catch { return (false, null); }
    }

    /// <summary>Checks whether the selected RKDL target is responsive.</summary>
    public bool ProbeRkdl(DetectedDevice device)
    {
        using var session = OpenRkdlSession(device);
        if (session is null) return false;
        try { return session.IsRockchipDevice() && session.TestDeviceReady(); }
        catch { return false; }
    }

    /// <summary>Probes on demand; server errors never grant direct USB access.</summary>
    private AdbClient? ProbeAdbServer()
    {
        if (!DebugBridgeRoute.UseAdbServer()) return null;
        var client = new AdbClient();
        _ = client.GetAdbVersion();
        return client;
    }

    internal static DeviceData RequireAdbServerTarget(AdbClient client, string serial)
        => client.GetDevices().FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal))
            ?? throw new IOException("The ADB server does not contain the selected target. Direct USB fallback is prohibited while the server is running.");

    private static DeviceData? ResolveAdbServerTarget(AdbClient? client, DetectedDevice device)
    {
        if (client is null) return null;
        var target = client.GetDevices().FirstOrDefault(d =>
            string.Equals(d.Serial, device.Serial, StringComparison.Ordinal));
        if (target is null && device.Key.StartsWith("adb:", StringComparison.Ordinal))
            throw new IOException("The selected ADB server target is no longer present.");
        return target;
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