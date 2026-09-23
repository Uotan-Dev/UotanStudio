using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>Provides the USB inventory to an embedded MCP host.</summary>
    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => _comm.EnumerateUsbDevices();

    /// <summary>Excludes only USB debug interfaces owned by a running bridge server.</summary>
    public IReadOnlyList<UsbDeviceInfo> ListRoutableUsbDevices()
    {
        var infos = ListUsbDevices();
        var hdcServer = DebugBridgeRoute.UseHdcServer();
        return infos.Where(info => Classify(info) switch
        {
            DeviceProtocolKind.Adb => !DebugBridgeRoute.AdbServerOwns(info.SerialNumber),
            DeviceProtocolKind.Hdc => !hdcServer,
            _ => true
        }).ToArray();
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
            using var stream = new AdbServiceStream(session.OpenStream(service));
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

    // Transaction callbacks must not retain or return the borrowed session.
    internal TResult RunFastboot<TResult>(DetectedDevice device, Func<FastbootSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = OpenFastbootSession(device)
            ?? throw new InvalidOperationException("Cannot open the selected Fastboot device.");
        cancellationToken.ThrowIfCancellationRequested();
        return operation(session);
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

    internal async Task<TResult> RunEdlAsync<TResult>(DetectedDevice device,
        Func<EdlSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = OpenEdlSession(device)
            ?? throw new InvalidOperationException("Cannot open the selected EDL device.");
        cancellationToken.ThrowIfCancellationRequested();
        return await operation(session).ConfigureAwait(false);
    }

    internal TResult RunAdb<TResult>(DetectedDevice device, Func<AdbSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = OpenAdbSession(device)
            ?? throw new InvalidOperationException("Cannot open the selected ADB device.");
        session.Connect();
        if (!session.IsConnected)
            throw new InvalidOperationException("ADB session is not connected.");
        cancellationToken.ThrowIfCancellationRequested();
        return operation(session);
    }

    internal async Task<TResult> RunAdbAsync<TResult>(DetectedDevice device,
        Func<AdbSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = OpenAdbSession(device)
            ?? throw new InvalidOperationException("Cannot open the selected ADB device.");
        session.Connect();
        if (!session.IsConnected)
            throw new InvalidOperationException("ADB session is not connected.");
        cancellationToken.ThrowIfCancellationRequested();
        return await operation(session).ConfigureAwait(false);
    }

    internal async Task<TResult> RunRkdlAsync<TResult>(DetectedDevice device,
        Func<RkdlSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = OpenRkdlSession(device)
            ?? throw new InvalidOperationException("Cannot open the selected RKDL device.");
        cancellationToken.ThrowIfCancellationRequested();
        return await operation(session).ConfigureAwait(false);
    }
}