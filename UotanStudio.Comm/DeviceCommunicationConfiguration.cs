using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm;

/// <summary>Shared USB policy and local debug-server discovery. USB policy never overrides server ownership.</summary>
public sealed record DeviceCommunicationConfiguration
{
    private static DeviceCommunicationConfiguration _current = new();

    /// <summary>Process configuration, applied before device discovery starts.</summary>
    public static DeviceCommunicationConfiguration Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Default USB backend. Auto retains platform defaults; forced modes do not fall back.</summary>
    public UsbApiKind UsbBackend
    {
        get;
        init => field = value is UsbApiKind.Auto or UsbApiKind.Native or UsbApiKind.LibUsbDotNet
            ? value : UsbApiKind.Auto;
    }

    /// <summary>Local ADB server host; the single definition lives in <see cref="Routing.DebugBridgeEndpoints"/>.</summary>
    public const string ServerHost = DebugBridgeEndpoints.ServerHost;

    /// <summary>Local ADB server port; the single definition lives in <see cref="Routing.DebugBridgeEndpoints"/>.</summary>
    public const int AdbServerPort = DebugBridgeEndpoints.AdbServerPort;

    /// <summary>Checks ADB endpoint ownership; only connection refusal means absent.</summary>
    public static Task<bool> ProbeAdbServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeRoute.IsListeningAsync(ServerHost, AdbServerPort, cancellationToken);

    /// <summary>Checks HDC endpoint ownership using the HDC environment/default port.</summary>
    public static Task<bool> ProbeHdcServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeRoute.IsListeningAsync(ServerHost, DebugBridgeEndpoints.HdcServerPort, cancellationToken);
}