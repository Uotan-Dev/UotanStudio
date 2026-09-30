using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Server-lifecycle watch: periodically probes the local adb/HDC server ports with the tri-state
/// debug-bridge probe and releases adopted server-session leases whose target vanished from the
/// server list while a server is retained as live. An inconclusive probe keeps the last known
/// state; only a conclusive listening/refused change raises
/// <see cref="DebugBridgeAvailabilityChanged"/> and invalidates cached probes (the snapshot then
/// flips between server endpoints and direct-USB endpoints).
/// <para>server 生命周期监视：用三态调试桥探测周期检查本机 adb/HDC server 端口，并在有 server
/// 被保留为存活时释放目标已从 server 列表消失的已采纳 server 会话租约。未能得出结论的探测保持
/// 上次已知状态；只有确定性的监听/拒绝结论变化才会触发
/// <see cref="DebugBridgeAvailabilityChanged"/> 并使缓存探测结果失效（快照随之在 server 端点与
/// direct-usb 端点之间切换）。</para>
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
            ProbeDebugBridgeEndpointAsync, interval, cancellationToken);

    private static Task<DebugBridgeProbeOutcome> ProbeDebugBridgeEndpointAsync(int port, CancellationToken cancellationToken)
        => DebugBridgeRoute.TryProbeAsync(DebugBridgeEndpoints.ServerHost, port, cancellationToken);

    /// <summary>
    /// Runs one full watch loop until cancellation: probes both ports, keeps the last known state
    /// through inconclusive probes, raises the availability event and invalidates cached probes
    /// only when a conclusive listening/refused result changes the retained state, and releases
    /// vanished server leases while any server is retained as live.
    /// <para>运行完整监视循环直至取消：探测两个端口，在未能得出结论的探测中保持上次已知状态；仅当
    /// 确定性的监听/拒绝结论改变保留状态时才上报可用性事件并使缓存探测结果失效；只要有 server 被
    /// 保留为存活就释放已消失的 server 租约。</para>
    /// </summary>
    /// <remarks>
    /// The public entry point is <see cref="StartServerStateWatch"/>; this overload exposes the
    /// probe function, ports and cadence so callers can drive the exact loop without opening real
    /// sockets.
    /// <para>公共入口是 <see cref="StartServerStateWatch"/>；本重载暴露探测函数、端口与周期，
    /// 使调用方无需打开真实套接字即可驱动同一循环。</para>
    /// </remarks>
    /// <param name="adbPort">Port probed for the adb server. <para>用于探测 adb server 的端口。</para></param>
    /// <param name="hdcPort">Port probed for the HDC server. <para>用于探测 HDC server 的端口。</para></param>
    /// <param name="probe">Tri-state endpoint probe; production passes
    /// <see cref="DebugBridgeRoute.TryProbeAsync"/> through
    /// <see cref="DebugBridgeEndpoints.ServerHost"/>.
    /// <para>三态端点探测；生产路径经 <see cref="DebugBridgeEndpoints.ServerHost"/> 传入
    /// <see cref="DebugBridgeRoute.TryProbeAsync"/>。</para></param>
    /// <param name="interval">Delay between passes. <para>两轮探测之间的延时。</para></param>
    /// <param name="cancellationToken">Stops the loop; cancellation ends the task normally.
    /// <para>停止循环；取消后任务正常结束。</para></param>
    /// <returns>Task that completes after the loop exits. <para>循环退出后完成的任务。</para></returns>
    internal async Task RunServerWatchAsync(int adbPort, int hdcPort,
        Func<int, CancellationToken, Task<DebugBridgeProbeOutcome>> probe, TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var reported = (Adb: false, Hdc: false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var adbLive = await ResolveLastKnownStateAsync(probe, adbPort, reported.Adb, cancellationToken).ConfigureAwait(false);
                var hdcLive = await ResolveLastKnownStateAsync(probe, hdcPort, reported.Hdc, cancellationToken).ConfigureAwait(false);
                if (adbLive != reported.Adb || hdcLive != reported.Hdc)
                {
                    reported = (adbLive, hdcLive);
                    DebugBridgeRoute.InvalidateProbe(DebugBridgeEndpoints.ServerHost, adbPort);
                    DebugBridgeRoute.InvalidateProbe(DebugBridgeEndpoints.ServerHost, hdcPort);
                    RaiseSafe(() => DebugBridgeAvailabilityChanged?.Invoke(adbLive, hdcLive),
                        "Debug bridge availability subscriber failed");
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

    /// <summary>
    /// Reduces one tri-state probe to the retained live flag: listening means live, refused means
    /// absent, and an inconclusive probe keeps <c>lastKnownState</c>; before the first conclusive
    /// pass there is no known state, so an unknown there resolves to absent exactly like the old
    /// two-state behaviour. Caller cancellation propagates so the watch still stops on time.
    /// <para>把一次三态探测归约为保留的存活标志：监听表示存活，拒绝表示不存在，未能得出结论的探测
    /// 保持 <c>lastKnownState</c>；首次确定性结论之前没有已知状态，此时未知按不存在处理，与旧的
    /// 二态行为一致。调用方取消继续上抛，监视照旧停表。</para>
    /// </summary>
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
    /// the watch pass: one faulty host subscriber must not hide later availability flips or block
    /// lease release for the remaining sources of the same pass.
    /// <para>调用事件并把订阅方异常记录为错误，而不是让它中断本轮监视：单个故障的宿主订阅方不得
    /// 掩盖后续可用性翻转，也不得阻止同一轮中其余发现源的租约释放。</para>
    /// </summary>
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

    private async Task ReleaseVanishedServerLeasesAsync(CancellationToken cancellationToken)
    {
        List<DeviceLease> serverLeases;
        lock (_gate)
        {
            serverLeases = _openLeases.Values
                .Where(lease => lease.Endpoint.Route == "server")
                .ToList();
        }

        foreach (var group in serverLeases.GroupBy(lease => lease.Endpoint.Source))
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