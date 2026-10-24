using UotanStudio.Mirroring;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>Wording shared with the mirror layer for a target that is not ready yet.
    /// <para>与投屏层一致、用于"目标尚未就绪"的文案。</para></summary>
    private const string HdcStartNotReadyPrefix = "HDC mirror target is not ready yet (retryable / 可重试): ";

    /// <summary>Time another HDC mirror start for the same target may run before this one is rejected.
    /// <para>同一目标的另一次 HDC 投屏启动可以占用多久，超过后本次启动被拒绝。</para></summary>
    private const int HdcStartGateWaitMilliseconds = 2_000;

    /// <summary>Time a target may stay missing from the device list before the start is rejected.
    /// <para>目标可以从设备列表消失多久，超过后启动被拒绝。</para></summary>
    private const int HdcTargetReadyBudgetMilliseconds = 3_000;

    /// <summary>Delay between two device-list checks while waiting for the target to reappear.
    /// <para>等待目标重新出现时两次设备列表检查之间的间隔。</para></summary>
    private const int HdcTargetReadyPollMilliseconds = 200;

    private static readonly Lock HdcStartGateLock = new();
    private static readonly Dictionary<string, SemaphoreSlim> HdcStartGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Launches an ohscrcpy mirror session on an HDC target. The facade probes the device UI Test capabilities,
    /// pushes the capture extension, starts the control and video daemons and returns the unconnected session.
    /// <para>在 HDC 目标上启动 ohscrcpy 投屏会话。门面探测设备 UI Test 能力、推送采集扩展、启动控制与视频
    /// 守护进程，并返回尚未连接的会话。</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session borrows the HDC lease opened by <see cref="OpenHdcLease"/>: when the local hdc server owns
    /// the target the lease carries a server-backed session, otherwise it carries the exact USB or TCP session.
    /// The facade probes UI Test, pushes and starts the video extension, forwards the daemon sockets to loopback
    /// ports and returns a <see cref="ManagedOhscrcpySession"/> whose frames reuse
    /// <see cref="ScrcpyVideoFrame"/>. Call <see cref="ManagedOhscrcpySession.Connect"/> after assigning it to
    /// the mirror view model, mirroring the scrcpy start flow. A failed start disposes the session and releases
    /// the lease immediately; otherwise the lease is released with the returned session.
    /// </para>
    /// <para>
    /// 会话借用 <see cref="OpenHdcLease"/> 打开的 HDC 租约：本机 hdc server 拥有该目标时租约携带 server 会话，
    /// 否则携带精确的 USB 或 TCP 会话。门面探测 UI Test、推送并启动视频扩展、把守护进程 socket 转发到回环端口，
    /// 并返回帧类型复用 <see cref="ScrcpyVideoFrame"/> 的 <see cref="ManagedOhscrcpySession"/>。把它交给投屏
    /// 视图模型后再调用 <see cref="ManagedOhscrcpySession.Connect"/>，与 scrcpy 启动流程一致。启动失败会立即
    /// 释放会话与租约；否则租约随返回的会话一同释放。
    /// </para>
    /// </remarks>
    /// <param name="device">The HDC target to mirror. <para>要投屏的 HDC 目标。</para></param>
    /// <param name="options">Capture parameters; <see langword="null"/> uses the defaults of
    /// <see cref="OhscrcpyLaunchOptions"/>. <para>采集参数；为 <see langword="null"/> 时使用
    /// <see cref="OhscrcpyLaunchOptions"/> 的默认值。</para></param>
    /// <returns>The unconnected session; the caller connects it after wiring the decode loop.
    /// <para>尚未连接的会话；调用方在接入解码循环后再连接。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.
    /// <para><paramref name="device"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not an HDC target.
    /// <para><paramref name="device"/> 不是 HDC 目标。</para></exception>
    /// <exception cref="IOException">The HDC lease, the extension push, the daemon start or the port forward
    /// failed, including ServerOnly mode without a listening hdc server. <para>HDC 租约、扩展推送、守护进程启动或
    /// 端口转发失败，也包括仅 server 模式但没有 hdc server 监听时。</para></exception>
    public ManagedOhscrcpySession StartOhscrcpyMirror(DetectedDevice device, OhscrcpyLaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol != DeviceProtocolKind.Hdc)
        {
            throw new ArgumentException("An HDC target is required. 需要 HDC 目标。", nameof(device));
        }

        var gateKey = HdcStartKey(device);
        if (!TryEnterHdcStartGate(gateKey, HdcStartGateWaitMilliseconds))
        {
            throw new IOException(
                HdcStartNotReadyPrefix
                + "another mirror start for this device is still running; wait for it to finish, then start "
                + "mirroring again. "
                + HdcStartNotReadyPrefix
                + "该设备的另一次投屏启动仍在进行；请等待其结束后重新开始投屏。");
        }

        try
        {
            var target = RequireReadyHdcTarget(device);
            var opened = AcquireForMirror(() => OpenHdcLease(target), () => ReleaseMirrorEndpointHolders(target));
            if (opened is null)
            {
                throw new ArgumentException("An HDC target is required. 需要 HDC 目标。", nameof(device));
            }

            var (lease, session) = opened.Value;
            try
            {
                var mirrorSession = lease.SharesConnection ? session.CreateLease() : session;
                var started = MirrorSessionFacade.StartOhscrcpyOverHdcSession(mirrorSession, target.Serial,
                    options ?? new(), connectTimeoutMs: 20_000, lease: lease,
                    logSink: text => LogService.Instance.Write(LogLevel.Info, "ScreenMirror", text));
                TrackMirrorSession(target, started);
                return started;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        finally
        {
            ExitHdcStartGate(gateKey);
        }
    }

    /// <summary>
    /// Serializes mirror starts per device, so a second start cannot push an agent, start a daemon or register a
    /// forward while the first one is still doing it. The gate is keyed by transport and serial because the USB
    /// device key changes across re-enumeration.
    /// <para>按设备串行化投屏启动，使第二次启动不会在第一次仍在推送代理、启动守护进程或登记转发时并发执行。
    /// 门锁按传输与序列号索引，因为 USB 设备键会在重新枚举后改变。</para>
    /// </summary>
    /// <param name="targetKey">Stable target identity from <see cref="HdcStartKey(DetectedDevice)"/>.
    /// <para>来自 <see cref="HdcStartKey(DetectedDevice)"/> 的稳定目标标识。</para></param>
    /// <param name="waitMilliseconds">Time to wait for the current start to finish.
    /// <para>等待当前启动结束的时间。</para></param>
    /// <returns><see langword="true"/> when the caller owns the gate and must call <see cref="ExitHdcStartGate"/>.
    /// <para>调用方取得门锁（并且必须调用 <see cref="ExitHdcStartGate"/>）时为 <see langword="true"/>。</para></returns>
    internal static bool TryEnterHdcStartGate(string targetKey, int waitMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentOutOfRangeException.ThrowIfNegative(waitMilliseconds);
        return HdcStartGateFor(targetKey).Wait(waitMilliseconds);
    }

    /// <summary>Releases a gate taken by <see cref="TryEnterHdcStartGate"/>.
    /// <para>释放由 <see cref="TryEnterHdcStartGate"/> 取得的门锁。</para>
    /// </summary>
    /// <param name="targetKey">The same target identity used to enter.
    /// <para>进入时使用的同一目标标识。</para></param>
    internal static void ExitHdcStartGate(string targetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        HdcStartGateFor(targetKey).Release();
    }

    private static SemaphoreSlim HdcStartGateFor(string targetKey)
    {
        lock (HdcStartGateLock)
        {
            if (!HdcStartGates.TryGetValue(targetKey, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                HdcStartGates[targetKey] = gate;
            }

            return gate;
        }
    }

    private static string HdcStartKey(DetectedDevice device)
        => string.IsNullOrWhiteSpace(device.Serial)
            ? device.Transport + "|" + device.Key
            : device.Transport + "|" + device.Serial;

    /// <summary>
    /// Waits briefly for the target to stay visible in the device list, so a scan that ran while the device was
    /// re-enumerating does not turn into an immediate "not connected" failure.
    /// <para>短暂等待目标在设备列表中保持可见，使在设备重新枚举期间执行的一次扫描不会立刻变成"设备未连接"失败。</para>
    /// </summary>
    /// <param name="device">The target resolved by the caller. <para>调用方已解析的目标。</para></param>
    /// <returns>The freshest device entry for the same target. <para>同一目标最新的设备条目。</para></returns>
    /// <exception cref="IOException">The target stayed missing for the whole readiness budget.
    /// <para>目标在整个就绪预算内都没有出现。</para></exception>
    private DetectedDevice RequireReadyHdcTarget(DetectedDevice device)
    {
        var deadline = Environment.TickCount64 + HdcTargetReadyBudgetMilliseconds;
        while (true)
        {
            var current = Scan().FirstOrDefault(candidate =>
                candidate.Protocol == DeviceProtocolKind.Hdc && IsSameHdcTarget(candidate, device));
            if (current is not null)
            {
                return current;
            }

            if (Environment.TickCount64 >= deadline)
            {
                break;
            }

            Thread.Sleep(HdcTargetReadyPollMilliseconds);
        }

        throw new IOException(
            HdcStartNotReadyPrefix
            + $"the HDC target {device.Serial} ({device.Key}) is no longer listed after "
            + $"{HdcTargetReadyBudgetMilliseconds} ms; reconnect the USB cable, wait for the device list to show it "
            + "again, then start mirroring. "
            + HdcStartNotReadyPrefix
            + $"HDC 目标 {device.Serial}（{device.Key}）在 {HdcTargetReadyBudgetMilliseconds} ms 后仍不在设备列表中；"
            + "请重新插拔 USB、等待设备列表重新显示它，然后再开始投屏。");
    }

    private static bool IsSameHdcTarget(DetectedDevice candidate, DetectedDevice device)
        => string.Equals(candidate.Key, device.Key, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(device.Serial)
                && string.Equals(candidate.Serial, device.Serial, StringComparison.Ordinal));
}