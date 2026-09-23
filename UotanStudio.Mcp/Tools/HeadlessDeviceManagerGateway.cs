using UotanStudio.Comm;
using UotanStudio.Comm.Devices.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Mcp.Tools;

/// <summary>Owns device access when MCP runs without the desktop host.</summary>
internal sealed class HeadlessDeviceManagerGateway : IDeviceManagerGateway
{
    private readonly ICommKit _comm;
    private readonly DeviceController _controller;
    private readonly Func<bool> _adbServerAvailable;
    private readonly Func<bool> _hdcServerAvailable;

    public HeadlessDeviceManagerGateway()
    {
        _comm = new CommKit();
        _controller = new DeviceController(
            [new UsbDiscoverySource(_comm, UsbApiKind.Auto), new AdbServerDiscoverySource(), new HdcServerDiscoverySource()],
            new CommDeviceTransportOpener(_comm));
        _adbServerAvailable = DebugBridgeRoute.UseAdbServer;
        _hdcServerAvailable = () => DebugBridgeRoute.UseHdcServer();
    }

    internal HeadlessDeviceManagerGateway(ICommKit comm, DeviceController controller,
        Func<bool> adbServerAvailable, Func<bool> hdcServerAvailable)
    {
        _comm = comm;
        _controller = controller;
        _adbServerAvailable = adbServerAvailable;
        _hdcServerAvailable = hdcServerAvailable;
    }

    // MCP's gateway is synchronous; all controller async work completes at this boundary.
    private static T Complete<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
    private static void Close(DeviceLease lease) => lease.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
    private DeviceSnapshot Refresh() => Complete(_controller.RefreshAsync());

    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices()
    {
        var snapshot = Refresh();
        var eligible = snapshot.Endpoints.Where(endpoint => IsRoutableUsb(endpoint))
            .Select(endpoint => endpoint.Id).ToHashSet();
        return _comm.EnumerateUsbDevices().Where(info =>
            eligible.Contains(new DeviceEndpointId(UsbDiscoverySource.SourceName,
                info.SourceApiKind.ToString(), info.DeviceKey))).ToArray();
    }

    public IReadOnlyList<(string Serial, string State)> ListAdbTargets()
    {
        var snapshot = Refresh();
        return snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
                (endpoint.Route == "server" || IsRoutableUsb(endpoint)))
            .Select(endpoint =>
            {
                var identity = endpoint.Serial ?? endpoint.Address;
                if (endpoint.Route == "server")
                {
                    return (identity, endpoint.State ?? "device");
                }

                try
                {
                    return (identity, RunAdb(identity, _ => "device"));
                }
                catch (Exception)
                {
                    return (identity, "offline");
                }
            }).ToArray();
    }

    public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAdb(serial, session =>
        {
            using var stream = new AdbServiceStream(session.OpenStream(service));
            return operation(stream);
        });
    }

    public void PushAdbFile(string serial, string localPath, string remotePath)
    {
        _ = RunAdb(serial, session =>
        {
            using var sync = session.CreateSyncClient();
            sync.Push(localPath, remotePath);
            return true;
        });
    }

    private TResult RunAdb<TResult>(string serial, Func<AdbSession, TResult> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var snapshot = Refresh();
        var endpoint = SelectAdb(snapshot, serial, _adbServerAvailable());
        var lease = Complete(_controller.OpenAsync(snapshot, endpoint.Id));
        try
        {
            if (lease.Transport is OwnedDeviceTransport<AdbSession> server)
            {
                return operation(server.Value);
            }

            if (lease.Transport is not OwnedDeviceTransport<IUsbDeviceSession> usb)
            {
                throw new InvalidOperationException("ADB endpoint has no supported transport.");
            }

            using var session = new AdbSession(new UsbDeviceTransport(usb.Value));
            session.Connect();
            if (!session.IsConnected)
            {
                throw new IOException("ADB USB authentication did not complete.");
            }

            return operation(session);
        }
        finally
        {
            Close(lease);
        }
    }

    public TResult RunFastboot<TResult>(Func<FastbootSession, TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = Refresh();
        var targets = snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "fastboot" && IsRoutableUsb(endpoint))
            .Take(2).ToArray();
        if (targets.Length != 1)
        {
            throw new InvalidOperationException("Exactly one Fastboot device must be connected.");
        }

        var lease = Complete(_controller.OpenAsync(snapshot, targets[0].Id));
        try
        {
            if (lease.Transport is not OwnedDeviceTransport<IUsbDeviceSession> usb ||
                !new FastbootFactory().TryCreate(usb.Value, out var protocol) || protocol is not FastbootSession session)
            {
                throw new InvalidOperationException("The selected device does not expose Fastboot.");
            }

            using (session)
            {
                return operation(session);
            }
        }
        finally
        {
            Close(lease);
        }
    }

    private bool IsRoutableUsb(DeviceEndpoint endpoint)
        => endpoint.Source == UsbDiscoverySource.SourceName && endpoint.Route == "direct-usb" &&
            !(endpoint.Protocol == "adb" && _adbServerAvailable()) &&
            !(endpoint.Protocol == "hdc" && _hdcServerAvailable());

    internal static DeviceEndpoint SelectAdb(DeviceSnapshot snapshot, string serial, bool serverAvailable)
    {
        var matches = snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
            (serverAvailable ? endpoint.Route == "server" : endpoint.Source == UsbDiscoverySource.SourceName &&
                endpoint.Route == "direct-usb") &&
            (string.Equals(endpoint.Serial ?? endpoint.Address, serial, StringComparison.Ordinal) ||
                (!serverAvailable && string.Equals(endpoint.Address, serial, StringComparison.Ordinal))))
            .Take(2).ToArray();
        if (matches.Length != 1)
        {
            throw new IOException("ADB target is missing or ambiguous.");
        }

        return matches[0];
    }
}
