using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Mcp.Tools;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Mcp;

/// <summary>Routes embedded MCP device tools through the desktop device manager.<para>把内嵌 MCP 设备工具路由到桌面设备管理器。</para></summary>
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
        _monitor.PushAdbFileAsync(RequireUniqueAdbTarget(serial), localPath, remotePath, cancellationToken)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public void PullAdbFile(string serial, string remotePath, string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        _monitor.PullAdbFileAsync(RequireUniqueAdbTarget(serial), remotePath, localPath, cancellationToken)
            .ConfigureAwait(false).GetAwaiter().GetResult();
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
    /// Locates the single ADB device whose serial matches the given identity; matching
    /// is case-sensitive.
    /// <para>在已扫描目标中定位序列号与给定标识唯一匹配的 ADB 设备，匹配区分大小写。</para>
    /// </summary>
    /// <param name="serial">The exact target identity to match.
    /// <para>要匹配的确切目标标识。</para></param>
    /// <returns>The selected ADB device.<para>选中的 ADB 设备。</para></returns>
    /// <exception cref="IOException">No unique scanned target matches the identity.
    /// <para>没有唯一目标与该标识匹配。</para></exception>
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