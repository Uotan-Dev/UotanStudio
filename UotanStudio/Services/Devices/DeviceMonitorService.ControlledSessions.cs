using UotanStudio.Comm.Devices.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    // Synchronous protocol transactions use this boundary to acquire an async controller lease.
    private DeviceLease LeaseEndpoint(DetectedDevice device, bool useAdbServer = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _controller.Snapshot;
        if (snapshot.Generation == 0 || useAdbServer &&
            !snapshot.Endpoints.Any(endpoint => endpoint.Protocol == "adb" &&
                endpoint.Route == "server" && endpoint.Address == device.Serial))
        {
            snapshot = _controller.RefreshAsync(cancellationToken).GetAwaiter().GetResult();
        }

        DeviceEndpoint? selected;
        if (useAdbServer)
        {
            selected = snapshot.Endpoints.SingleOrDefault(endpoint => endpoint.Protocol == "adb" &&
                endpoint.Route == "server" && string.Equals(endpoint.Address, device.Serial, StringComparison.Ordinal));
        }
        else if (device.Key.StartsWith("adb:", StringComparison.Ordinal) ||
            device.Key.StartsWith("hdc:", StringComparison.Ordinal))
        {
            throw new IOException("A server-origin target cannot fall back to direct USB.");
        }
        else
        {
            selected = snapshot.Endpoints.SingleOrDefault(endpoint =>
                endpoint.Source == UsbDiscoverySource.SourceName &&
                string.Equals(endpoint.Address, device.Key, StringComparison.Ordinal));
        }

        if (selected is null)
        {
            throw new IOException("The selected device endpoint is no longer present. Refresh the device list.");
        }

        var expectedProtocol = device.Protocol switch
        {
            DeviceProtocolKind.Adb => "adb",
            DeviceProtocolKind.Fastboot => "fastboot",
            DeviceProtocolKind.Edl => "edl",
            DeviceProtocolKind.Rkdl => "rkdl",
            DeviceProtocolKind.Hdc => "hdc",
            _ => throw new ArgumentOutOfRangeException(nameof(device))
        };
        if (!string.Equals(selected.Protocol, expectedProtocol, StringComparison.Ordinal))
        {
            throw new IOException("The selected endpoint no longer exposes the requested protocol.");
        }

        return _controller.OpenAsync(snapshot, selected.Id, cancellationToken).GetAwaiter().GetResult();
    }

    private static IUsbDeviceSession RequireUsb(DeviceLease lease)
        => lease.Transport is OwnedDeviceTransport<IUsbDeviceSession> usb
            ? usb.Value : throw new IOException("The selected endpoint is not a direct USB session.");

    private (DeviceLease Lease, AdbSession Session) LeaseAdb(DetectedDevice device,
        CancellationToken cancellationToken)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
        {
            throw new ArgumentException("An ADB target is required.", nameof(device));
        }

        var serverOwns = DebugBridgeRoute.AdbServerOwns(device.Serial);
        if (device.Key.StartsWith("adb:", StringComparison.Ordinal) && !serverOwns)
        {
            throw new IOException("The selected ADB server target is unavailable.");
        }

        var lease = LeaseEndpoint(device, serverOwns, cancellationToken);
        try
        {
            var session = lease.Transport switch
            {
                OwnedDeviceTransport<AdbSession> server => server.Value,
                OwnedDeviceTransport<IUsbDeviceSession> usb =>
                    new AdbSession(new UsbDeviceTransport(usb.Value)),
                _ => throw new IOException("The selected ADB endpoint has no supported session.")
            };
            return (lease, session);
        }
        catch
        {
            CloseLease(lease);
            throw;
        }
    }

    private static void CloseLease(DeviceLease lease)
        => lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
