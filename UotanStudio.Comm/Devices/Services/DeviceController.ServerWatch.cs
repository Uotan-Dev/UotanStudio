using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Server-lifecycle watch: periodically probes the local adb/HDC server ports and releases
/// adopted server-session leases whose target vanished from the server list or whose server
/// stopped listening. Availability flips raise <see cref="DebugBridgeAvailabilityChanged"/> so
/// the host can trigger an incremental rescan (the snapshot then flips between server
/// endpoints and direct-USB endpoints).
/// <para>server 生命周期监视：周期探测本机 adb/HDC server 端口，并释放目标从 server 列表消失、
/// 或 server 不再监听的已采纳 server 会话租约。可用性翻转触发
/// <see cref="DebugBridgeAvailabilityChanged"/>，宿主据此触发增量重扫
/// （快照随之在 server 端点与 direct-usb 端点之间切换）。</para>
/// </summary>
public sealed partial class DeviceController
{
    private CancellationTokenSource? _serverWatch;

    /// <summary>
    /// Raised when the adb/HDC server availability flips; arguments are (adbLive, hdcLive).
    /// <para>adb/HDC server 可用性翻转时触发；参数为 (adb 存活, hdc 存活)。</para>
    /// </summary>
    public event Action<bool, bool>? DebugBridgeAvailabilityChanged;

    /// <summary>
    /// Starts the periodic server-state watch. Idempotent.
    /// <para>启动周期 server 状态监视。幂等。</para>
    /// </summary>
    /// <param name="interval">
    /// Probe cadence; null uses 2500 milliseconds. <para>探测周期；null 使用 2500 毫秒。</para>
    /// </param>
    public void StartServerStateWatch(TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (_serverWatch is not null)
            {
                return;
            }

            _serverWatch = new CancellationTokenSource();
            _ = Task.Run(() => RunServerWatchAsync(interval ?? TimeSpan.FromMilliseconds(2500), _serverWatch.Token));
        }
    }

    private async Task RunServerWatchAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var reported = (Adb: false, Hdc: false);
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var adbLive = await ProbeQuietlyAsync(DebugBridgeEndpoints.AdbServerPort, cancellationToken).ConfigureAwait(false);
                var hdcLive = await ProbeQuietlyAsync(DebugBridgeEndpoints.HdcServerPort, cancellationToken).ConfigureAwait(false);
                if (first || adbLive != reported.Adb || hdcLive != reported.Hdc)
                {
                    first = false;
                    reported = (adbLive, hdcLive);
                    DebugBridgeRoute.InvalidateProbe(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.AdbServerPort);
                    DebugBridgeRoute.InvalidateProbe(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.HdcServerPort);
                    DebugBridgeAvailabilityChanged?.Invoke(adbLive, hdcLive);
                }

                if (adbLive || hdcLive)
                {
                    await ReleaseVanishedServerLeasesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                UsbTrace.Log($"Server watch pass failed: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task<bool> ProbeQuietlyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            return await DebugBridgeRoute.IsListeningAsync(DebugBridgeEndpoints.ServerHost, port, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReleaseVanishedServerLeasesAsync(CancellationToken cancellationToken)
    {
        List<DeviceLease> serverLeases;
        lock (_gate)
        {
            serverLeases = _openLeases.Values
                .Where(lease => lease.Endpoint.Route == "server")
                .ToList();
        }

        foreach (var lease in serverLeases)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var endpoint = lease.Endpoint;
            var source = _sources.FirstOrDefault(candidate => candidate.Name == endpoint.Source);
            if (source is null)
            {
                continue;
            }

            try
            {
                var discovered = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                if (!discovered.Any(candidate => candidate.Address == endpoint.Address))
                {
                    ReleaseServerLease(lease);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }
    }

    private void ReleaseServerLease(DeviceLease lease)
    {
        var endpoint = lease.Endpoint;
        lock (_gate)
        {
            if (_openLeases.TryGetValue(endpoint.Id, out var current) && ReferenceEquals(current, lease))
            {
                _openLeases.Remove(endpoint.Id);
                _leased.Remove(endpoint.Id);
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UsbTrace.Log($"Server lease release failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
        DeviceRemoved?.Invoke(endpoint);
    }
}