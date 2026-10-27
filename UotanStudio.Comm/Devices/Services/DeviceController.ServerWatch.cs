using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Server-lifecycle watch: probes the local adb/HDC server ports and publishes one availability
/// snapshot per pass. In <see cref="DebugBridgeRouteMode.StartupFrozen"/> mode the snapshot is not
/// refreshed; in the other modes only a conclusive listening/refused flip raises
/// <see cref="DebugBridgeAvailabilityChanged"/> and releases the leases of a server that is gone.
/// <para>server 生命周期监视：探测本机 adb/HDC server 端口，每轮发布一份可用性快照。在
/// <see cref="DebugBridgeRouteMode.StartupFrozen"/> 模式下不刷新快照；其他模式仅在探测出现确定性的
/// 监听/拒绝翻转时触发 <see cref="DebugBridgeAvailabilityChanged"/>，并释放已消失 server 的租约。</para>
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
            _ = Task.Run(() => RunServerWatchLoopAsync(interval ?? TimeSpan.FromMilliseconds(2500), _serverWatch.Token));
        }
    }

    private Task RunServerWatchLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
        => RunServerWatchAsync(DebugBridgeEndpoints.AdbServerPort, DebugBridgeEndpoints.HdcServerPort,
            ProbeDebugBridgeEndpointAsync, interval, cancellationToken, DebugBridgeRoute.ApplyServerWatch);

    private static Task<DebugBridgeProbeOutcome> ProbeDebugBridgeEndpointAsync(int port, CancellationToken cancellationToken)
        => DebugBridgeRoute.TryProbeAsync(DebugBridgeEndpoints.ServerHost, port, cancellationToken);

    /// <summary>
    /// Runs the watch loop until cancellation, seeding the retained state from the current snapshot so
    /// an inconclusive first pass cannot demote a conclusive startup result. Each pass publishes one
    /// coalesced snapshot, raises availability events only on conclusive flips and releases server
    /// leases whose server is gone.
    /// <para>运行监视循环直至取消，并用当前快照播种保留状态，使无结论的第一轮不会把确定性的启动结论降级。
    /// 每轮发布一份合并快照，仅在确定性结论翻转时上报可用性事件，并释放 server 已消失的租约。</para>
    /// </summary>
    /// <param name="adbPort">Port probed for the adb server. <para>用于探测 adb server 的端口。</para></param>
    /// <param name="hdcPort">Port probed for the HDC server. <para>用于探测 HDC server 的端口。</para></param>
    /// <param name="probe">Tri-state probe run for each port.
    /// <para>对每个端口执行的三态探测。</para></param>
    /// <param name="interval">Delay between passes. <para>两轮探测之间的延时。</para></param>
    /// <param name="cancellationToken">Stops the loop; cancellation ends the task normally.
    /// <para>停止循环；取消后任务正常结束。</para></param>
    /// <param name="availabilitySink">Receives the coalesced retained state of every pass; null keeps
    /// the loop side-effect free.
    /// <para>每轮接收合并后的保留状态；为 null 时循环无副作用。</para></param>
    internal async Task RunServerWatchAsync(int adbPort, int hdcPort,
        Func<int, CancellationToken, Task<DebugBridgeProbeOutcome>> probe, TimeSpan interval,
        CancellationToken cancellationToken, Action<bool, bool>? availabilitySink = null)
    {
        var retained = DebugBridgeRoute.Current;
        var reported = (Adb: retained.AdbServerAvailable, Hdc: retained.HdcServerAvailable);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var adbLive = await ResolveLastKnownStateAsync(probe, adbPort, reported.Adb, cancellationToken).ConfigureAwait(false);
                var hdcLive = await ResolveLastKnownStateAsync(probe, hdcPort, reported.Hdc, cancellationToken).ConfigureAwait(false);
                availabilitySink?.Invoke(adbLive, hdcLive);
                if (adbLive != reported.Adb || hdcLive != reported.Hdc)
                {
                    reported = (adbLive, hdcLive);
                    DebugBridgeRoute.InvalidateProbe(DebugBridgeEndpoints.ServerHost, adbPort);
                    DebugBridgeRoute.InvalidateProbe(DebugBridgeEndpoints.ServerHost, hdcPort);
                    RaiseSafe(() => DebugBridgeAvailabilityChanged?.Invoke(adbLive, hdcLive),
                        "Debug bridge availability subscriber failed");
                }

                await ReleaseStaleServerLeasesAsync(adbLive, hdcLive, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Reduces one tri-state probe to the retained live flag: listening means live, refused means
    /// absent, and an inconclusive probe keeps the last known state.
    /// <para>把一次三态探测归约为保留的存活标志：监听表示存活，拒绝表示不存在，无结论的探测保持
    /// 上次已知状态。</para>
    /// </summary>
    /// <param name="probe">The tri-state probe to run. <para>要执行的三态探测。</para></param>
    /// <param name="port">Port to probe. <para>待探测的端口。</para></param>
    /// <param name="lastKnownState">State retained when the probe is inconclusive.
    /// <para>探测无结论时保留的状态。</para></param>
    /// <param name="cancellationToken">Cancels the probe. <para>取消探测。</para></param>
    /// <returns>Task yielding the resolved live flag. <para>给出归约后存活标志的任务。</para></returns>
    private static async Task<bool> ResolveLastKnownStateAsync(
        Func<int, CancellationToken, Task<DebugBridgeProbeOutcome>> probe, int port, bool lastKnownState,
        CancellationToken cancellationToken)
    {
        var outcome = await probe(port, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            DebugBridgeProbeOutcome.Listening => true,
            DebugBridgeProbeOutcome.Refused => false,
            _ => lastKnownState,
        };
    }

    /// <summary>
    /// Invokes an event and records a subscriber exception as an error instead of letting it abort
    /// the current watch pass, so one faulty subscriber cannot hide later availability flips.
    /// <para>调用事件并把订阅方异常记录为错误而不是中断本轮监视，单个故障订阅方不会掩盖后续
    /// 可用性翻转。</para>
    /// </summary>
    /// <param name="raise">The event invocation to run. <para>要执行的事件调用。</para></param>
    /// <param name="context">Label prefixed to the recorded error. <para>记录错误时附加的标签。</para></param>
    private static void RaiseSafe(Action raise, string context)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            UsbTrace.LogError($"{context}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Releases server leases that cannot be served: an absent server (its endpoint was
    /// conclusively refused) releases every lease from that source, and a live server releases the
    /// leases whose target vanished from its own list. This is the drain that lets the next route open
    /// only after the old one has been given up.
    /// <para>释放已无法提供服务的 server 租约：server 确定性不存在时释放该来源的全部租约；
    /// server 存活时释放其列表里已消失的目标对应的租约。该清理保证旧路由先被放弃，新路由随后才能打开。</para>
    /// </summary>
    /// <param name="adbLive">Retained adb liveness for this pass.
    /// <para>本轮保留的 adb 存活状态。</para></param>
    /// <param name="hdcLive">Retained HDC liveness for this pass.
    /// <para>本轮保留的 HDC 存活状态。</para></param>
    /// <param name="cancellationToken">Stops the release pass. <para>停止本次释放遍历。</para></param>
    private async Task ReleaseStaleServerLeasesAsync(bool adbLive, bool hdcLive, CancellationToken cancellationToken)
    {
        List<DeviceLease> serverLeases;
        lock (_gate)
        {
            serverLeases = [.. _openLeases.Values.Where(lease => lease.Endpoint.Route == "server")];
        }

        if (serverLeases.Count == 0)
        {
            return;
        }

        List<DeviceLease> liveLeases = [];
        foreach (var lease in serverLeases)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (IsServerSourceAbsent(lease.Endpoint.Source, adbLive, hdcLive))
            {
                ReleaseServerLease(lease);
            }
            else
            {
                liveLeases.Add(lease);
            }
        }

        foreach (var group in liveLeases.GroupBy(lease => lease.Endpoint.Source))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var source = _sources.FirstOrDefault(candidate => candidate.Name == group.Key);
            if (source is null)
            {
                continue;
            }

            IReadOnlyList<DeviceEndpoint> discovered;
            try
            {
                discovered = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            foreach (var lease in group)
            {
                var endpoint = lease.Endpoint;
                if (!discovered.Any(candidate => candidate.Id == endpoint.Id))
                {
                    ReleaseServerLease(lease);
                }
            }
        }
    }

    /// <summary>
    /// Decides whether a server discovery source is conclusively absent for this pass.
    /// <para>判定某个 server 发现源在本轮是否确定性不存在。</para>
    /// </summary>
    /// <param name="source">Discovery source name recorded on the lease.
    /// <para>租约上记录的发现源名称。</para></param>
    /// <param name="adbLive">Retained adb liveness. <para>保留的 adb 存活状态。</para></param>
    /// <param name="hdcLive">Retained HDC liveness. <para>保留的 HDC 存活状态。</para></param>
    /// <returns><see langword="true"/> when the matching server is conclusively absent.
    /// <para>对应 server 确定性不存在时为 <see langword="true"/>。</para></returns>
    private static bool IsServerSourceAbsent(string source, bool adbLive, bool hdcLive)
    {
        if (source.StartsWith("adb-server@", StringComparison.Ordinal))
        {
            return !adbLive;
        }

        if (source.StartsWith("hdc-server@", StringComparison.Ordinal))
        {
            return !hdcLive;
        }

        return false;
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
        RaiseSafe(() => DeviceRemoved?.Invoke(endpoint), "Device removal subscriber failed");
    }
}