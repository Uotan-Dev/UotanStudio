using System.Collections.Concurrent;
using System.Diagnostics;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Total time a mirror start waits for another direct-USB or server lease on the same endpoint to finish.
    /// <para>投屏启动等待同一端点上其它直连 USB 或 server 租约结束的总时长。</para>
    /// </summary>
    private static readonly TimeSpan MirrorLeaseWaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>First retry delay after a busy endpoint; it doubles up to the maximum below.
    /// <para>端点占用后的首次重试间隔；此后按下方上限逐次翻倍。</para></summary>
    private const int MirrorLeaseFirstRetryDelayMs = 100;

    /// <summary>Upper bound of one retry delay so a long wait never polls aggressively.
    /// <para>单次重试间隔上限，使长时间等待不会高频轮询。</para></summary>
    private const int MirrorLeaseMaximumRetryDelayMs = 500;

    /// <summary>
    /// Mirror sessions this monitor started, keyed by the physical target they mirror. A device endpoint is
    /// exclusive, so at most one of them can still hold it.
    /// <para>本监视器启动过的投屏会话，按其所投屏的物理目标索引。设备端点独占，因此最多只有一个仍持有它。</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, MirrorSessionRegistration> _ownedMirrorSessions =
        new(StringComparer.Ordinal);

    /// <summary>
    /// One started mirror session with the identities it was registered under, so a later start on the same
    /// physical device can find it whether that start resolves the ADB or the HDC interface of that device.
    /// <para>一条已启动的投屏会话及其登记身份；同一物理设备的后续启动无论解析到 ADB 还是 HDC 接口都能找到它。</para>
    /// </summary>
    /// <param name="serial">Physical device serial. <para>物理设备序列号。</para></param>
    /// <param name="key">Discovery key of the mirrored endpoint. <para>被投屏端点的发现键。</para></param>
    /// <param name="session">The session owning the endpoint reservation. <para>持有端点占用的会话。</para></param>
    private sealed class MirrorSessionRegistration(string serial, string key, string? physicalId, IDisposable session)
    {
        public string Serial { get; } = serial;
        public string Key { get; } = key;
        public string? PhysicalId { get; } = physicalId;
        public IDisposable Session { get; } = session;
    }

    /// <summary>
    /// Records the started mirror session as the owner of the device endpoint, replacing an older registration
    /// for the same physical device.
    /// <para>把已启动的投屏会话登记为设备端点持有者，替换同一物理设备的旧登记。</para>
    /// </summary>
    /// <param name="device">The mirrored device. <para>被投屏的设备。</para></param>
    /// <param name="session">The session owned by the caller; it is disposed once a newer session replaces it or a
    /// later start needs the endpoint back. <para>调用方持有的会话；被更新会话替换或后续启动需要回收端点时释放。</para></param>
    internal void TrackMirrorSession(DetectedDevice device, IDisposable session)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(session);
        foreach (var registration in _ownedMirrorSessions.ToArray())
        {
            if (ReferenceEquals(registration.Value.Session, session))
            {
                continue;
            }

            if (MatchesDevice(registration.Value, device) &&
                _ownedMirrorSessions.TryRemove(registration.Key, out var replaced))
            {
                DisposeMirrorSession(replaced.Session, device, "replaced by a newer session");
            }
        }

        _ownedMirrorSessions[MirrorSessionKey(device)] =
            new MirrorSessionRegistration(device.Serial ?? string.Empty, device.Key, device.PhysicalId, session);
    }

    /// <summary>
    /// Stops the mirror session this monitor still tracks for the device so its exclusive endpoint can be leased
    /// again. The endpoint has a single owner, so a leftover session from a closed or replaced mirror document is
    /// the most likely holder when a start reports a busy endpoint.
    /// <para>停止本监视器仍为该设备跟踪的投屏会话，使其独占端点可被重新租用。端点只能有一个持有者，因此启动
    /// 报端点占用时，最可能的持有者就是已关闭或被替换的投屏文档遗留的会话。</para>
    /// </summary>
    /// <param name="device">The device whose leftover mirror session is stopped.
    /// <para>要停止遗留投屏会话的设备。</para></param>
    /// <returns><see langword="true"/> when a tracked session was stopped.
    /// <para>确实停止了已登记的会话时为 <see langword="true"/>。</para></returns>
    internal bool ReleaseTrackedMirrorSession(DetectedDevice device)
    {
        if (device is null)
        {
            return false;
        }

        MirrorSessionRegistration? found = null;
        if (_ownedMirrorSessions.TryRemove(MirrorSessionKey(device), out var keyed))
        {
            found = keyed;
        }
        else
        {
            foreach (var registration in _ownedMirrorSessions.ToArray())
            {
                if (MatchesDevice(registration.Value, device) &&
                    _ownedMirrorSessions.TryRemove(registration.Key, out var matched))
                {
                    found = matched;
                    break;
                }
            }
        }

        return found is not null && DisposeMirrorSession(found.Session, device, "endpoint reclaimed by a new start");
    }

    private static bool MatchesDevice(MirrorSessionRegistration registration, DetectedDevice device)
        => MatchesIdentity(registration.Serial, registration.Key, registration.PhysicalId, device);

    private static string MirrorSessionKey(DetectedDevice device)
        => string.IsNullOrWhiteSpace(device.Serial) ? device.Key : device.Serial;

    private static bool DisposeMirrorSession(IDisposable session, DetectedDevice device, string reason)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogService.Instance.Write(LogLevel.Warn, "ScreenMirror",
                $"Stopping the previous mirror session for {device.Serial} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        LogService.Instance.Write(LogLevel.Info, "ScreenMirror",
            $"Stopped the previous mirror session for {device.Serial} ({reason}) so its exclusive device endpoint "
            + $"can be leased again. 已停止 {device.Serial} 上一次的投屏会话（{reason}），以重新租用其独占设备端点。");
        return true;
    }

    /// <summary>
    /// Bounds one host-started background operation that borrows an exclusive device endpoint, so a mirror start
    /// can preempt it instead of waiting for it to finish.
    /// <para>为借用独占端点的一项宿主后台操作设定边界，使投屏启动可以抢占它，而不是等它结束。</para>
    /// </summary>
    /// <param name="device">The device whose endpoint the work borrows.
    /// <para>该工作借用端点的设备。</para></param>
    internal sealed class BackgroundEndpointWork(DetectedDevice device) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _cancelled;

        public DetectedDevice Device { get; } = device;

        public CancellationToken Token => _cancellation.Token;

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0)
            {
                return;
            }

            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }

    private readonly ConcurrentDictionary<long, BackgroundEndpointWork> _backgroundEndpointWork = new();
    private long _nextBackgroundEndpointWorkId;

    /// <summary>
    /// Registers one host-started background operation that borrows the device's exclusive endpoint. The returned
    /// scope carries the token the work must observe and unregisters it on disposal.
    /// <para>登记一项借用该设备独占端点的宿主后台操作；返回的作用域携带工作必须观察的取消令牌，并在释放时
    /// 注销登记。</para>
    /// </summary>
    /// <param name="device">The device whose endpoint the work borrows.
    /// <para>该工作借用端点的设备。</para></param>
    /// <returns>The scope owning the registration; disposing it also disposes the token source.
    /// <para>持有该登记的作用域；释放它会一并释放取消令牌源。</para></returns>
    internal EndpointWorkScope BeginBackgroundEndpointWork(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var work = new BackgroundEndpointWork(device);
        var id = Interlocked.Increment(ref _nextBackgroundEndpointWorkId);
        _backgroundEndpointWork[id] = work;
        return new EndpointWorkScope(this, id, work);
    }

    /// <summary>
    /// Cancels the background endpoint work this host still runs for the device, so a mirror start is not blocked
    /// by a device-detail refresh or a root probe that has not finished yet.
    /// <para>取消本宿主仍为该设备运行的后台端点工作，使投屏启动不被尚未结束的设备详情刷新或 root 探测阻塞。</para>
    /// </summary>
    /// <param name="device">The device whose background work is cancelled.
    /// <para>要取消后台工作的设备。</para></param>
    /// <returns>The number of cancelled registrations. <para>被取消的登记数量。</para></returns>
    internal int CancelBackgroundEndpointWork(DetectedDevice device)
    {
        if (device is null)
        {
            return 0;
        }

        var cancelled = 0;
        foreach (var entry in _backgroundEndpointWork)
        {
            if (!IsSamePhysicalTarget(entry.Value.Device, device) ||
                !_backgroundEndpointWork.TryRemove(entry.Key, out var work))
            {
                continue;
            }

            work.Cancel();
            cancelled++;
        }

        if (cancelled > 0)
        {
            LogService.Instance.Write(LogLevel.Info, "ScreenMirror",
                $"Cancelled {cancelled} background endpoint operation(s) for {device.Serial} so the mirror start can "
                + $"lease the device endpoint. 已取消 {device.Serial} 的 {cancelled} 项后台端点操作，使投屏启动可以租用该设备端点。");
        }

        return cancelled;
    }

    /// <summary>
    /// Releases every endpoint holder this host still owns for the device before a mirror start keeps waiting:
    /// the mirror session it tracked and the background work it started.
    /// <para>在投屏启动继续等待之前，释放本宿主仍为该设备持有的全部端点占用：它跟踪的投屏会话与它启动的后台工作。</para>
    /// </summary>
    /// <param name="device">The device whose endpoint is needed. <para>需要其端点的设备。</para></param>
    internal void ReleaseMirrorEndpointHolders(DetectedDevice device)
    {
        if (device is null)
        {
            return;
        }

        ReleaseTrackedMirrorSession(device);
        CancelBackgroundEndpointWork(device);
    }

    /// <summary>
    /// Whether two entries describe the same physical device: the same serial, the same discovery key, or the same
    /// verified USB physical identity, which is how the ADB and HDC interfaces of one device match.
    /// <para>判断两个条目是否描述同一台物理设备：同一序列号、同一发现键，或同一已核实的 USB 物理身份；
    /// 一台设备的 ADB 与 HDC 接口正是靠最后一项匹配。</para>
    /// </summary>
    /// <param name="left">First entry. <para>第一个条目。</para></param>
    /// <param name="right">Second entry. <para>第二个条目。</para></param>
    /// <returns><see langword="true"/> when both entries identify one physical device.
    /// <para>两个条目标识同一台物理设备时为 <see langword="true"/>。</para></returns>
    internal static bool IsSamePhysicalTarget(DetectedDevice left, DetectedDevice right)
        => left is not null && MatchesIdentity(left.Serial, left.Key, left.PhysicalId, right);

    private static bool MatchesIdentity(string? serial, string? key, string? physicalId, DetectedDevice device)
    {
        if (device is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(serial) && string.Equals(serial, device.Serial, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(key) && string.Equals(key, device.Key, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrEmpty(physicalId) &&
            string.Equals(physicalId, device.PhysicalId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The registration scope handed to background endpoint work.
    /// <para>交给后台端点工作的登记作用域。</para>
    /// </summary>
    internal sealed class EndpointWorkScope(DeviceMonitorService owner, long id, BackgroundEndpointWork work) : IDisposable
    {
        private int _disposed;

        /// <summary>Cancellation signalled when a mirror start needs the endpoint.
        /// <para>投屏启动需要该端点时触发的取消。</para></summary>
        internal CancellationToken Token => work.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            owner._backgroundEndpointWork.TryRemove(id, out _);
            work.Dispose();
        }
    }

    private DeviceLease LeaseEndpoint(DetectedDevice device, bool useAdbServer = false,
        DeviceSessionKind? sessionKind = null, CancellationToken cancellationToken = default)
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

        var expectedProtocol = ExpectedEndpointProtocol(device.Protocol);
        if (!string.Equals(selected.Protocol, expectedProtocol, StringComparison.Ordinal))
        {
            throw new IOException("The selected endpoint no longer exposes the requested protocol.");
        }

        return sessionKind is { } kind
            ? _controller.OpenAsync(snapshot, selected.Id, kind, cancellationToken).GetAwaiter().GetResult()
            : _controller.OpenAsync(snapshot, selected.Id, cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Maps a device protocol to the endpoint protocol string produced during discovery. Every
    /// defined protocol must have a mapping, otherwise leasing an endpoint fails before any I/O.
    /// <para>把设备协议映射为发现阶段产生的端点协议字符串；每个已定义的协议都必须有映射，
    /// 否则在发起任何 I/O 之前取得租约即会失败。</para>
    /// </summary>
    /// <param name="protocol">The device protocol to map. <para>待映射的设备协议。</para></param>
    /// <returns>The endpoint protocol string. <para>端点协议字符串。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocol"/> is not a defined protocol. <para><paramref name="protocol"/> 不是已定义的协议。</para></exception>
    internal static string ExpectedEndpointProtocol(DeviceProtocolKind protocol) => protocol switch
    {
        DeviceProtocolKind.Adb => "adb",
        DeviceProtocolKind.Fastboot => "fastboot",
        DeviceProtocolKind.Edl => "edl",
        DeviceProtocolKind.Rkdl => "rkdl",
        DeviceProtocolKind.Hdc => "hdc",
        DeviceProtocolKind.Mtk => "mtk",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unexpected protocol kind.")
    };

    private (DeviceLease Lease, IAdbDeviceSession Session) LeaseAdb(DetectedDevice device,
        CancellationToken cancellationToken)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
        {
            throw new InvalidOperationException("Cannot open the selected ADB device.");
        }

        var serverOwns = DebugBridgeSessionOpener.AdbServerOwns(device.Serial);
        if (device.Key.StartsWith("adb:", StringComparison.Ordinal) && !serverOwns)
        {
            throw new IOException("The selected ADB server target is unavailable.");
        }

        if (device.Key.StartsWith("adb-tcp:", StringComparison.Ordinal))
        {
            return serverOwns
                ? OpenServerAdbLease(device.Serial)
                : OpenDirectNetworkAdbLease(device.Serial);
        }

        var lease = LeaseEndpoint(device, serverOwns, DeviceSessionKind.Adb, cancellationToken);
        if (!lease.TryGetSession<IAdbDeviceSession>(out var session))
        {
            CloseLease(lease);
            throw new IOException("The selected ADB endpoint has no supported session.");
        }

        return (lease, session);
    }

    private static void CloseLease(DeviceLease lease)
        => lease.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Runs one exclusive acquisition for a mirror start, retrying only while another lease still holds the
    /// endpoint. Device-detail enrichment, the su probe and log capture borrow the same exclusive direct-USB
    /// endpoint, so a start that arrives during one of them waits instead of reporting a lease conflict.
    /// <para>为投屏启动执行一次独占取得：仅当另一个租约仍持有端点时重试。设备详情补全、su 探测与日志抓取
    /// 借用同一条独占直连 USB 端点，因此落在其中一次操作期间的投屏启动会等待，而不是直接报租约冲突。</para>
    /// </summary>
    /// <typeparam name="TValue">Result of a successful acquisition.
    /// <para>成功取得的结果。</para></typeparam>
    /// <param name="acquire">The exclusive acquisition to run; its failures other than a busy endpoint propagate.
    /// <para>要执行的独占取得；除端点占用外的失败照常抛出。</para></param>
    /// <returns>The value returned by <paramref name="acquire"/>. <para><paramref name="acquire"/> 返回的值。</para></returns>
    /// <exception cref="IOException">The endpoint stayed leased beyond <see cref="MirrorLeaseWaitTimeout"/>.
    /// <para>端点被占用超过 <see cref="MirrorLeaseWaitTimeout"/>。</para></exception>
    internal static TValue AcquireForMirror<TValue>(Func<TValue> acquire)
        => AcquireForMirror(acquire, MirrorLeaseWaitTimeout, delay => Thread.Sleep(delay), releaseStaleOwner: null);

    /// <summary>
    /// Runs one exclusive acquisition and, on the first busy rejection, stops the mirror session this host still
    /// tracks for the device before waiting for any other holder. A closed or replaced mirror document can leave
    /// its endpoint reservation behind, and that reservation is indistinguishable from an unrelated operation
    /// until the host reclaims it.
    /// <para>执行一次独占取得，并在首次被占用时先停止本宿主仍为该设备跟踪的投屏会话，再等待其他持有者。
    /// 已关闭或被替换的投屏文档可能遗留端点占用，而该占用在宿主回收之前与其它操作的占用无法区分。</para>
    /// </summary>
    /// <typeparam name="TValue">Result of a successful acquisition. <para>成功取得的结果。</para></typeparam>
    /// <param name="acquire">The exclusive acquisition to run. <para>要执行的独占取得。</para></param>
    /// <param name="releaseStaleOwner">Stops this host's own leftover holder; invoked at most once.
    /// <para>停止本宿主遗留的持有者；最多调用一次。</para></param>
    /// <returns>The value returned by <paramref name="acquire"/>. <para><paramref name="acquire"/> 返回的值。</para></returns>
    /// <exception cref="IOException">The endpoint stayed leased beyond <see cref="MirrorLeaseWaitTimeout"/>.
    /// <para>端点被占用超过 <see cref="MirrorLeaseWaitTimeout"/>。</para></exception>
    internal static TValue AcquireForMirror<TValue>(Func<TValue> acquire, Action releaseStaleOwner)
        => AcquireForMirror(acquire, MirrorLeaseWaitTimeout, delay => Thread.Sleep(delay), releaseStaleOwner);

    /// <summary>
    /// Runs one exclusive acquisition with an explicit wait policy, so the retry contract is testable without
    /// waiting in real time.
    /// <para>以显式等待策略执行一次独占取得，使重试契约无需真实等待即可测试。</para>
    /// </summary>
    /// <typeparam name="TValue">Result of a successful acquisition. <para>成功取得的结果。</para></typeparam>
    /// <param name="acquire">The exclusive acquisition to run. <para>要执行的独占取得。</para></param>
    /// <param name="timeout">Total wait budget; zero fails immediately on the first busy rejection.
    /// <para>总等待预算；为零时首次占用即失败。</para></param>
    /// <param name="wait">Delay applied between attempts; the policy never sleeps itself.
    /// <para>两次尝试之间的延迟；策略自身不休眠。</para></param>
    /// <returns>The value returned by <paramref name="acquire"/>. <para><paramref name="acquire"/> 返回的值。</para></returns>
    /// <exception cref="IOException">The endpoint stayed leased for the whole budget.
    /// <para>端点在整个预算内保持占用。</para></exception>
    internal static TValue AcquireForMirror<TValue>(Func<TValue> acquire, TimeSpan timeout, Action<TimeSpan> wait,
        Action? releaseStaleOwner = null)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(wait);
        var stopwatch = Stopwatch.StartNew();
        var delayMs = MirrorLeaseFirstRetryDelayMs;
        var reclaimed = releaseStaleOwner is null;
        while (true)
        {
            try
            {
                return acquire();
            }
            catch (DeviceEndpointBusyException) when (!reclaimed)
            {
                reclaimed = true;
                releaseStaleOwner!();
            }
            catch (DeviceEndpointBusyException) when (stopwatch.Elapsed < timeout)
            {
                wait(TimeSpan.FromMilliseconds(delayMs));
                delayMs = Math.Min(delayMs * 2, MirrorLeaseMaximumRetryDelayMs);
            }
            catch (DeviceEndpointBusyException busy)
            {
                throw new IOException(
                    "The device endpoint is busy: another operation still holds it, such as a device-details "
                    + "refresh, a root probe, a log capture or a file transfer. Wait for it to finish, close the "
                    + "log capture if it is open, and start mirroring again. "
                    + "设备端点正被其它操作占用（如设备详情刷新、root 探测、日志抓取或文件传输）；请等待其结束，"
                    + "如有日志抓取请先停止，然后重新投屏。", busy);
            }
        }
    }
}