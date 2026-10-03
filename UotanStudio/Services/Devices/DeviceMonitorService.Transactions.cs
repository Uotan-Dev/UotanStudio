using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>Provides the USB inventory to an embedded MCP host.</summary>
    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => _controller.EnumerateUsb();

    /// <summary>Lists the direct-USB interfaces left in the snapshot; debug bridge servers own their
    /// protocol's interfaces, and discovery already removed those endpoints.</summary>
    public IReadOnlyList<UsbDeviceInfo> ListRoutableUsbDevices()
    {
        var snapshot = _controller.RefreshAsync().GetAwaiter().GetResult();
        return snapshot.Endpoints.Where(endpoint =>
            endpoint.Source == UotanStudio.Comm.Devices.Discovery.UsbDiscoverySource.SourceName &&
            endpoint.Route == "direct-usb" && endpoint.UsbInfo is not null)
            .Select(endpoint => endpoint.UsbInfo!)
            .ToArray();
    }

    /// <summary>Lists the routed server and direct USB targets.</summary>
    public IReadOnlyList<(string Serial, string State)> ListAdbTargets()
        => Scan().Where(device => device.Protocol == DeviceProtocolKind.Adb)
            .Select(device => (device.Serial, device.Key.StartsWith("adb:", StringComparison.Ordinal)
                ? device.State : ProbeAdb(device) ? "device" : "offline")).ToArray();

    /// <summary>Forwards a service to the exact routed ADB target.</summary>
    public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(operation);
        return RunAdb(RequireUniqueAdbTarget(serial), session =>
        {
            using var stream = session.OpenServiceStream(service);
            return operation(stream);
        });
    }

    /// <summary>Uploads through the exact routed ADB target.</summary>
    public void PushAdbFile(string serial, string localPath, string remotePath)
    {
        _ = RunAdb(RequireUniqueAdbTarget(serial), session =>
        {
            using var sync = session.CreateSyncClient();
            sync.Push(localPath, remotePath);
            return true;
        });
    }

    private DetectedDevice RequireUniqueAdbTarget(string serial)
    {
        var matches = Scan().Where(device => device.Protocol == DeviceProtocolKind.Adb &&
            string.Equals(device.Serial, serial, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length != 1)
            throw new IOException("The selected ADB target is missing or ambiguous.");
        return matches[0];
    }

    /// <summary>Forwards a legacy MCP Fastboot operation only when the target is unambiguous.</summary>
    public TResult RunSingleFastboot<TResult>(Func<FastbootSession, TResult> operation)
    {
        var devices = Scan().Where(device => device.Protocol == DeviceProtocolKind.Fastboot).ToArray();
        if (devices.Length != 1)
            throw new InvalidOperationException("Exactly one Fastboot device must be connected.");
        return RunFastboot(devices[0], operation);
    }

    internal TResult RunFastboot<TResult>(DetectedDevice device, Func<FastbootSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Fastboot)
            throw new ArgumentException("A Fastboot target is required.", nameof(device));
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Fastboot, cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<FastbootSession>(out var session))
                throw new IOException("The selected device does not expose Fastboot.");
            cancellationToken.ThrowIfCancellationRequested();
            return operation(session);
        }
        finally
        {
            CloseLease(lease);
        }
    }

    internal void RunFastboot(DetectedDevice device, Action<FastbootSession> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = RunFastboot(device, session =>
        {
            operation(session);
            return true;
        }, cancellationToken);
    }

    internal Task<TResult> RunEdlAsync<TResult>(DetectedDevice device,
        Func<EdlSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        EnsureEdlLivenessProbe();
        return _edlConnections.RunAsync(device, operation, cancellationToken);
    }

    internal TResult RunAdb<TResult>(DetectedDevice device, Func<IAdbDeviceSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var (lease, session) = LeaseAdb(device, cancellationToken);
        try
        {
            using (session)
            {
                session.Connect();
                if (!session.IsConnected)
                    throw new InvalidOperationException("ADB session is not connected.");
                cancellationToken.ThrowIfCancellationRequested();
                return operation(session);
            }
        }
        finally
        {
            CloseLease(lease);
        }
    }

    internal async Task<TResult> RunAdbAsync<TResult>(DetectedDevice device,
        Func<IAdbDeviceSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var (lease, session) = LeaseAdb(device, cancellationToken);
        try
        {
            using (session)
            {
                session.Connect();
                if (!session.IsConnected)
                    throw new InvalidOperationException("ADB session is not connected.");
                cancellationToken.ThrowIfCancellationRequested();
                return await operation(session).ConfigureAwait(false);
            }
        }
        finally
        {
            CloseLease(lease);
        }
    }

    internal async Task<TResult> RunRkdlAsync<TResult>(DetectedDevice device,
        Func<RkdlSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Rkdl)
            throw new ArgumentException("An RKDL target is required.", nameof(device));
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Bootrom, cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<RkdlSession>(out var session))
                throw new IOException("The selected device does not expose an RKDL session.");
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(session).ConfigureAwait(false);
        }
        finally
        {
            CloseLease(lease);
        }
    }

    internal async Task<TResult> RunMtkAsync<TResult>(DetectedDevice device,
        Func<MtkSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Mtk)
            throw new ArgumentException("An MTK target is required.", nameof(device));
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Mtk, cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<MtkSession>(out var session))
                throw new IOException("The selected device does not expose an MTK session.");
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(session).ConfigureAwait(false);
        }
        finally
        {
            CloseLease(lease);
        }
    }
}