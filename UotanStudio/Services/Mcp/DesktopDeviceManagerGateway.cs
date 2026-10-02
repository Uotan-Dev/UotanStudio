using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Mcp.Tools;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Mcp;

/// <summary>Routes embedded MCP device tools through the desktop device manager.</summary>
public sealed class DesktopDeviceManagerGateway : IDeviceManagerGateway
{
    private readonly DeviceMonitorService _monitor = App.DeviceMonitor;

    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => _monitor.ListRoutableUsbDevices();

    public IReadOnlyList<(string Serial, string State)> ListAdbTargets() => _monitor.ListAdbTargets();

    public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(operation);
        return _monitor.RunAdb(RequireUniqueAdbTarget(serial), session =>
        {
            using var stream = session.OpenServiceStream(service);
            return operation(stream);
        }, cancellationToken);
    }

    public void PushAdbFile(string serial, string localPath, string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        _monitor.RunAdb(RequireUniqueAdbTarget(serial), session =>
        {
            using var sync = session.CreateSyncClient();
            sync.Push(localPath, remotePath);
            return true;
        }, cancellationToken);
    }

    public void PullAdbFile(string serial, string remotePath, string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        _monitor.RunAdb(RequireUniqueAdbTarget(serial), session =>
        {
            using var sync = session.CreateSyncClient();
            sync.Pull(remotePath, localPath);
            return true;
        }, cancellationToken);
    }

    public TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var devices = _monitor.Scan().Where(device => device.Protocol == DeviceProtocolKind.Fastboot);
        if (string.IsNullOrWhiteSpace(target))
        {
            var candidates = devices.Take(2).ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException("Exactly one Fastboot device must be connected.");
            }

            return _monitor.RunFastboot(candidates[0], operation, cancellationToken);
        }

        var matches = devices
            .Where(device => string.Equals(device.Serial, target, StringComparison.Ordinal) ||
                string.Equals(device.Key, target, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length == 0)
        {
            throw new IOException($"No Fastboot device matches target '{target}'.");
        }

        if (matches.Length > 1)
        {
            throw new IOException($"Fastboot target '{target}' is ambiguous.");
        }

        return _monitor.RunFastboot(matches[0], operation, cancellationToken);
    }

    public (string ServiceName, string Password, string Payload) CreateQrPairingSession()
    {
        var credentials = AdbQrPairing.GenerateCredentials();
        return (credentials.ServiceName, credentials.Password, AdbQrPairing.BuildPayload(credentials));
    }

    public async Task<(string Route, string Address)> PairQrWirelessAsync(string serviceName, string password,
        int timeoutSeconds = 180, CancellationToken cancellationToken = default)
    {
        if (timeoutSeconds is < 1 or > MaxPairingWaitSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds),
                $"The QR pairing wait must be between 1 and {MaxPairingWaitSeconds} seconds.");
        }

        var outcome = await _monitor.RunQrPairingAsync(
            new AdbQrPairingCredentials(serviceName, password), progress: null, cancellationToken,
            TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
        return (outcome.Route.ToString(), outcome.Address);
    }

    private const int MaxPairingWaitSeconds = 1800;

    /// <summary>
    /// <para>在桌面宿主的已扫描目标中定位唯一的 ADB 设备，标识匹配区分大小写。</para>
    /// Locates the single ADB device matching the identity in the desktop host's scan result; matching
    /// is case-sensitive.
    /// </summary>
    /// <param name="serial"><para>由 <see cref="ListAdbTargets"/> 报出的确切目标标识。</para>
    /// The exact target identity as reported by <see cref="ListAdbTargets"/>.</param>
    /// <returns><para>选中的 ADB 设备。</para>The selected ADB device.</returns>
    /// <exception cref="IOException"><para>没有唯一目标与该标识匹配。</para>
    /// No unique scanned target matches the identity.</exception>
    private DetectedDevice RequireUniqueAdbTarget(string serial)
    {
        var matches = _monitor.Scan().Where(device => device.Protocol == DeviceProtocolKind.Adb &&
            string.Equals(device.Serial, serial, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            throw new IOException("The selected ADB target is missing or ambiguous.");
        }

        return matches[0];
    }
}