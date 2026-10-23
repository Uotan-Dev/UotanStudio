using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Routing;

/// <summary>Probes debug bridge server liveness over plain TCP and holds the process-global adb and
/// HDC server availability snapshot together with the fixed route mode.
/// <para>以普通 TCP 探测调试桥 server 存活，并持有进程全局的 adb 与 HDC server 可用性快照以及固定路由模式。</para></summary>
/// <remarks>
/// <para>正值结果（server 存在）按短 TTL 缓存，避免一次扫描内对同一端点重复建连；
/// 「连接被拒绝」结果不缓存（保持既有约定：仅连接被拒绝视为不存在，不缓存负结果），
/// 但同一调用方在同一次扫描流程内可通过 <see cref="BeginProbeScope"/> 复用一次探测结论。
/// server 归属判定与路由开启由上层 <c>Devices.Services.DebugBridgeSessionOpener</c> 提供，
/// 本类型只回答「端口上是否有 server 在监听」，并保存所有决策共同读取的不可变快照。
/// 快照每次变化都会推进 epoch；已发现的端点盖上该 epoch，打开时可据此发现路由在发现之后已改变。</para>
/// <para>Positive results (server present) are cached for a short TTL so a single scan does not
/// repeatedly connect to the same endpoint; a refused connection is never cached (existing
/// contract: only a refused connection means absent, negative results are not cached). Callers
/// running one scan pass can share a single probe via <see cref="BeginProbeScope"/>. Server
/// ownership decisions and routed session opening live in the upper
/// <c>Devices.Services.DebugBridgeSessionOpener</c>; this type only answers whether a server
/// is listening on an endpoint and stores the immutable snapshot every decision reads. Each
/// snapshot change advances the epoch; discovered endpoints are stamped with it so an open can
/// detect a route change that happened after discovery.</para>
/// </remarks>
public static class DebugBridgeRoute
{
    private const int PositiveTtlMs = 2000;
    private static readonly Lock StateLock = new();
    private static readonly Dictionary<string, bool> ReportedStates = new(StringComparer.Ordinal);

    private static int _mode;
    private static int _modeConfigured;
    private static DebugBridgeRouteSnapshot _snapshot = DebugBridgeRouteSnapshot.Empty;

    /// <summary>
    /// Gets the route mode fixed for this process. It is captured once by
    /// <see cref="ConfigureMode(DebugBridgeRouteMode)"/> before the first probe; changing the
    /// persisted setting requires an application restart.
    /// <para>获取本进程固定使用的路由模式。它由 <see cref="ConfigureMode(DebugBridgeRouteMode)"/> 在首次探测之前
    /// 捕获一次；修改持久化设置需要重启应用。</para>
    /// </summary>
    /// <value>The configured mode, or <see cref="DebugBridgeRouteMode.Auto"/> before configuration.
    /// <para>已配置的模式；配置之前为 <see cref="DebugBridgeRouteMode.Auto"/>。</para></value>
    public static DebugBridgeRouteMode Mode => (DebugBridgeRouteMode)Volatile.Read(ref _mode);

    /// <summary>
    /// Gets the current immutable availability snapshot. Every discovery source, opener and host
    /// decision reads this one value instead of querying separate mutable flags.
    /// <para>获取当前不可变的可用性快照。每个发现源、开启器与宿主决策都读取这一个值，而不是查询各自的可变标志。</para>
    /// </summary>
    /// <value>The retained snapshot; <see cref="DebugBridgeRouteSnapshot.Empty"/> before initialization.
    /// <para>保留的快照；初始化之前为 <see cref="DebugBridgeRouteSnapshot.Empty"/>。</para></value>
    public static DebugBridgeRouteSnapshot Current
    {
        get
        {
            lock (StateLock)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// Fixes the route mode before the first probe. Repeated calls with the same mode are no-ops;
    /// a different mode after configuration is rejected because the running route cannot be switched
    /// safely and the persisted setting applies on the next start.
    /// <para>在首次探测之前固定路由模式。以相同模式重复调用无操作；配置之后再以不同模式调用会被拒绝，
    /// 因为运行中的路由无法安全切换，持久化设置将在下次启动时生效。</para>
    /// </summary>
    /// <param name="mode">The mode to fix for this process.<para>本进程要固定的模式。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.
    /// <para><paramref name="mode"/> 不是已定义的值。</para></exception>
    /// <exception cref="InvalidOperationException">A different mode was already fixed; restart the application.
    /// <para>已固定了不同的模式；请重启应用。</para></exception>
    public static void ConfigureMode(DebugBridgeRouteMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        lock (StateLock)
        {
            if (Volatile.Read(ref _modeConfigured) != 0)
            {
                if ((DebugBridgeRouteMode)_mode != mode)
                {
                    throw new InvalidOperationException(
                        "The debug bridge route mode is fixed for this process; restart the application to apply the saved mode.");
                }

                return;
            }

            _mode = (int)mode;
            Volatile.Write(ref _modeConfigured, 1);
        }
    }

    /// <summary>
    /// Process-global adb server availability from <see cref="Current"/>; written at startup and
    /// refreshed each pass by the conclusive probe of the default endpoint; inconclusive probes keep
    /// the previous value. Every adb route reads it to choose between server forwarding and direct connection.
    /// <para>来自 <see cref="Current"/> 的进程级 adb server 可用性，启动时写入，并由对默认端点的确证探测每轮刷新；
    /// 未确证的探测保持原值。所有 adb 路由据此在“经 server 转发”与“直连”之间选择。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the last conclusive probe of <see cref="DebugBridgeEndpoints.ServerHost"/>
    /// at <see cref="DebugBridgeEndpoints.AdbServerPort"/> found a listening server.
    /// <para>当对默认端点的最后一次确定性探测发现 server 正在监听时为 <see langword="true"/>。</para></value>
    public static bool AdbServerAvailable => Current.AdbServerAvailable;

    /// <summary>
    /// Process-global HDC server availability from <see cref="Current"/>, mirroring
    /// <see cref="AdbServerAvailable"/>: written at startup and refreshed each pass by the
    /// server-lifecycle watch's conclusive probe of the default HDC endpoint; inconclusive probes
    /// keep the previous value.
    /// <para>来自 <see cref="Current"/> 的进程全局 HDC server 可用性，语义与 <see cref="AdbServerAvailable"/>
    /// 对称：启动时探测写入，之后由 server 监视循环每轮的确定性结论刷新；未知结果保持上次状态。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the last conclusive probe of <see cref="DebugBridgeEndpoints.ServerHost"/>
    /// at <see cref="DebugBridgeEndpoints.HdcServerPort"/> found a listening server.
    /// <para>当对默认 HDC 端点的最后一次确定性探测发现 server 正在监听时为 <see langword="true"/>。</para></value>
    public static bool HdcServerAvailable => Current.HdcServerAvailable;

    /// <summary>
    /// Startup boundary: probes both default endpoints once and applies the conclusive outcomes as
    /// one snapshot (one epoch), so discovery cannot observe a half-updated route.
    /// <para>启动边界：对两个默认端点各探测一次，并把确定性结论作为同一份快照（同一 epoch）应用，
    /// 使发现层不会观察到只更新了一半的路由。</para>
    /// </summary>
    internal static void InitializeDefaultEndpoints()
    {
        var adb = ProbeDefaultEndpoint(DebugBridgeEndpoints.AdbServerPort);
        var hdc = ProbeDefaultEndpoint(DebugBridgeEndpoints.HdcServerPort);
        ApplySnapshot(adb, hdc);
    }

    /// <summary>
    /// Startup and post-lifecycle resync boundary: probes the default HDC endpoint once for real
    /// and applies the conclusive outcome (listening or refused) to
    /// <see cref="HdcServerAvailable"/>; an inconclusive probe keeps the current value. In
    /// <see cref="DebugBridgeRouteMode.StartupFrozen"/> mode the snapshot captured by
    /// <see cref="InitializeDefaultEndpoints"/> is retained and the outcome is discarded.
    /// <para>程序启动与显式 server 生命周期操作后的重同步边界：对默认 HDC 端点做一次真实探测，
    /// 把确定性结论（监听或拒绝）应用到 <see cref="HdcServerAvailable"/>；未知结论保持当前值。
    /// 在 <see cref="DebugBridgeRouteMode.StartupFrozen"/> 模式下保留
    /// <see cref="InitializeDefaultEndpoints"/> 采集的快照并丢弃本次结论。</para>
    /// </summary>
    public static void InitializeHdcServerProbe() => ApplySnapshot(null, ProbeDefaultEndpoint(DebugBridgeEndpoints.HdcServerPort));

    /// <summary>
    /// Startup and post-lifecycle resync boundary: probes the default adb endpoint once for real
    /// and applies the conclusive outcome (listening or refused) to
    /// <see cref="AdbServerAvailable"/>; an inconclusive probe keeps the current value. In
    /// <see cref="DebugBridgeRouteMode.StartupFrozen"/> mode the snapshot captured by
    /// <see cref="InitializeDefaultEndpoints"/> is retained and the outcome is discarded.
    /// <para>程序启动与显式 server 生命周期操作后的重同步边界：对默认 adb 端点做一次真实探测，
    /// 把确定性结论（监听或拒绝）应用到 <see cref="AdbServerAvailable"/>；未知结论保持当前值。
    /// 在 <see cref="DebugBridgeRouteMode.StartupFrozen"/> 模式下保留
    /// <see cref="InitializeDefaultEndpoints"/> 采集的快照并丢弃本次结论。</para>
    /// </summary>
    public static void InitializeAdbServerProbe() => ApplySnapshot(ProbeDefaultEndpoint(DebugBridgeEndpoints.AdbServerPort), null);

    private static bool? ProbeDefaultEndpoint(int port)
    {
        try
        {
            var outcome = TryProbeAsync(DebugBridgeEndpoints.ServerHost, port).GetAwaiter().GetResult();
            return outcome switch
            {
                DebugBridgeProbeOutcome.Listening => true,
                DebugBridgeProbeOutcome.Refused => false,
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies one server-watch pass to the process-global snapshot. In
    /// <see cref="DebugBridgeRouteMode.StartupFrozen"/> mode the startup snapshot is never refreshed;
    /// the mode's contract is that routes do not change until the application restarts.
    /// <para>把一轮 server 监视结果应用到进程级快照。在
    /// <see cref="DebugBridgeRouteMode.StartupFrozen"/> 模式下启动快照永不刷新；该模式的契约是
    /// 路由在应用重启之前保持不变。</para>
    /// </summary>
    /// <param name="adbAvailable">Retained adb liveness for this pass.
    /// <para>本轮保留的 adb 存活状态。</para></param>
    /// <param name="hdcAvailable">Retained HDC liveness for this pass.
    /// <para>本轮保留的 HDC 存活状态。</para></param>
    internal static void ApplyServerWatch(bool adbAvailable, bool hdcAvailable)
        => ApplySnapshot(adbAvailable, hdcAvailable);

    private static void ApplySnapshot(bool? adbAvailable, bool? hdcAvailable)
    {
        lock (StateLock)
        {
            if ((DebugBridgeRouteMode)_mode == DebugBridgeRouteMode.StartupFrozen && _snapshot.Epoch != 0)
            {
                return;
            }

            var adb = adbAvailable ?? _snapshot.AdbServerAvailable;
            var hdc = hdcAvailable ?? _snapshot.HdcServerAvailable;
            if (_snapshot.Epoch != 0 && adb == _snapshot.AdbServerAvailable && hdc == _snapshot.HdcServerAvailable)
            {
                return;
            }

            _snapshot = new DebugBridgeRouteSnapshot(adb, hdc, _snapshot.Epoch + 1);
        }
    }

    /// <summary>
    /// Clears the process-global mode and snapshot so an isolated test can configure a different
    /// route. Production code never calls this.
    /// <para>清除进程级模式与快照，使隔离测试能够配置不同的路由。生产代码不会调用。</para>
    /// </summary>
    internal static void ResetForTests()
    {
        lock (StateLock)
        {
            _mode = 0;
            Volatile.Write(ref _modeConfigured, 0);
            _snapshot = DebugBridgeRouteSnapshot.Empty;
            PositiveCache.Clear();
            ReportedStates.Clear();
        }

        _scopeCache = null;
        _scopeDepth = 0;
    }

    /// <summary>
    /// Writes one availability pair without probing so an isolated test can drive the mode and epoch
    /// deterministically. The same <see cref="DebugBridgeRouteMode.StartupFrozen"/> retention rule as
    /// production applies. Production code never calls this.
    /// <para>不探测地写入一对可用性值，使隔离测试能确定性地驱动模式与 epoch。与生产相同的
    /// <see cref="DebugBridgeRouteMode.StartupFrozen"/> 保留规则仍然生效。生产代码不会调用。</para>
    /// </summary>
    /// <param name="adbAvailable">ADB server availability to record.
    /// <para>要记录的 ADB server 可用性。</para></param>
    /// <param name="hdcAvailable">HDC server availability to record.
    /// <para>要记录的 HDC server 可用性。</para></param>
    internal static void SetAvailabilityForTests(bool adbAvailable, bool hdcAvailable)
        => ApplySnapshot(adbAvailable, hdcAvailable);

    /// <summary>
    /// Reports initial endpoint state and subsequent transitions for host logging; never writes to the console.
    /// <para>为主机日志报告端点的初始状态及后续状态变化；绝不写入控制台。</para>
    /// </summary>
    /// <remarks>The handler runs on the probing thread whenever a cached or freshly probed result differs
    /// from the last reported state for that endpoint; handler exceptions are swallowed.
    /// <para>当某个端点的缓存结果或新探测结果与其上次上报状态不一致时，处理程序在探测线程上运行；
    /// 处理程序抛出的异常会被吞没。</para></remarks>
    public static event Action<string, int, bool>? ServerAvailabilityChanged;

    private static void ReportState(string host, int port, string key, bool listening)
    {
        var handler = ServerAvailabilityChanged;
        if (handler is null)
        {
            return;
        }
        lock (StateLock)
        {
            if (ReportedStates.TryGetValue(key, out var previous) && previous == listening)
            {
                return;
            }
            if (ReportedStates.Count >= 256)
            {
                ReportedStates.Clear();
            }
            ReportedStates[key] = listening;
        }
        try
        {
            handler(host, port, listening);
        }
        catch
        {
        }
    }

    private sealed record ProbeResult(bool Listening, long TimestampTicks);

    private static readonly ConcurrentDictionary<string, ProbeResult> PositiveCache = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static ConcurrentDictionary<string, bool>? _scopeCache;

    [ThreadStatic]
    private static int _scopeDepth;

    /// <summary>Checks an endpoint without caching absence or converting protocol failures into USB permission.
    /// <para>检查端点，但不缓存“不存在”结论，也不会把协议失败转换为 USB 权限问题。</para></summary>
    /// <remarks><para>存在结果经 2 秒 TTL 缓存；拒绝与其他错误每次都真实探测。</para>
    /// <para>Present results go through a 2-second TTL cache; refusals and other errors probe for real every time.</para></remarks>
    /// <param name="host">Server hostname or literal address; a non-parseable value is resolved as DNS.
    /// <para>server 主机名或字面地址；无法解析的值将按 DNS 解析。</para></param>
    /// <param name="port">Server TCP port number.<para>server TCP 端口号。</para></param>
    /// <returns><see langword="true"/> only when the TCP connect completes successfully; <see langword="false"/> only when the connection is refused.
    /// <para>仅在 TCP 连接成功时返回 <see langword="true"/>；仅在连接被拒绝时返回 <see langword="false"/>。</para></returns>
    public static bool IsListening(string host, int port)
        => IsListeningAsync(host, port).GetAwaiter().GetResult();

    /// <summary>Asynchronously probes endpoint ownership without treating timeout or protocol failure as absence.
    /// <para>异步探测端点归属，不会把超时或协议失败视为“不存在”。</para></summary>
    /// <param name="host">Server hostname or literal address; a non-parseable value is resolved as DNS.
    /// <para>server 主机名或字面地址；无法解析的值将按 DNS 解析。</para></param>
    /// <param name="port">Server TCP port number. <para>server TCP 端口号。</para></param>
    /// <param name="cancellationToken">Cancels the probe; a 3-second internal deadline also aborts a stalled connect.
    /// <para>取消本次探测；内部 3 秒截止时间也会中止停滞的连接。</para></param>
    /// <returns>
    /// <see langword="true"/> when a server accepts the connection, which is then cached for the TTL;
    /// <see langword="false"/> only when the connection is refused.
    /// <para>当 server 接受连接时返回 <see langword="true"/>，并在 TTL 内缓存；仅在连接被拒绝时返回
    /// <see langword="false"/>。</para></returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled or the 3-second deadline elapsed.
    /// <para><paramref name="cancellationToken"/> 被触发，或 3 秒截止时间已到。</para></exception>
    /// <exception cref="SocketException">The connect failed with an error other than refusal.
    /// <para>连接因拒绝以外的错误而失败。</para></exception>
    public static async Task<bool> IsListeningAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture));
        if (PositiveCache.TryGetValue(key, out var cached))
        {
            if (Environment.TickCount64 - cached.TimestampTicks < PositiveTtlMs)
            {
                ReportState(host, port, key, cached.Listening);
                return cached.Listening;
            }
            PositiveCache.TryRemove(key, out _);
        }

        var (error, linkedCancellationRequested) = await ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (linkedCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (error == SocketError.ConnectionRefused)
        {
            ReportState(host, port, key, false);
            return false;
        }
        if (error != SocketError.Success)
        {
            throw new SocketException((int)error);
        }
        PositiveCache[key] = new ProbeResult(true, Environment.TickCount64);
        ReportState(host, port, key, true);
        return true;
    }

    /// <summary>
    /// Probes an endpoint with three outcomes: listening, refused, unknown. Unknown covers the internal deadline, unreachable targets, DNS failures and connect resets; callers must interpret it as "keep the last known state", and only a refusal means the server is absent.
    /// <para>探测端点，三种结果：正在监听、拒绝、未知。未知涵盖内部时限、不可达目标、DNS 失败与连接重置；调用方应把它解释为“保持最后已知状态”，只有拒绝才意味着 server 不存在。</para>
    /// </summary>
    /// <remarks>
    /// <para>监听结果与 <see cref="IsListeningAsync"/> 共用同一正向 TTL 缓存；拒绝与未知都不缓存。
    /// 未知不会向 <see cref="ServerAvailabilityChanged"/> 上报，因此不会翻转已上报状态。
    /// 与二态 API 不同，本方法不会因非拒绝的 <see cref="SocketException"/> 抛出。</para>
    /// <para>A listening result shares the same positive TTL cache as <see cref="IsListeningAsync"/>;
    /// refusals and unknowns are never cached. An unknown outcome is not reported through
    /// <see cref="ServerAvailabilityChanged"/>, so the last reported state cannot flip on unknown.
    /// Unlike the two-state API, this method does not throw for non-refusal
    /// <see cref="SocketException"/> failures.</para>
    /// </remarks>
    /// <param name="host">Server hostname or literal address; a non-parseable value is resolved as DNS.
    /// <para>server 主机名或字面地址；无法解析的值将按 DNS 解析。</para></param>
    /// <param name="port">Server TCP port number in [0, 65535].
    /// <para>[0, 65535] 范围内的 server TCP 端口号。</para></param>
    /// <param name="cancellationToken">Cancels the probe; the 3-second internal deadline instead
    /// yields <see cref="DebugBridgeProbeOutcome.Unknown"/>.
    /// <para>取消本次探测；内部 3 秒截止时间则返回 <see cref="DebugBridgeProbeOutcome.Unknown"/>。</para></param>
    /// <returns>
    /// <see cref="DebugBridgeProbeOutcome.Listening"/> when the TCP connect completed (also served
    /// from the positive TTL cache), <see cref="DebugBridgeProbeOutcome.Refused"/> only when the
    /// connection was refused, and <see cref="DebugBridgeProbeOutcome.Unknown"/> for every other
    /// fault including the internal deadline.
    /// <para>TCP 连接成功时返回 <see cref="DebugBridgeProbeOutcome.Listening"/>（正向结果由 TTL 缓存提供）；
    /// 仅在连接被拒绝时返回 <see cref="DebugBridgeProbeOutcome.Refused"/>；
    /// 其余故障（含内部 3 秒超时）均返回 <see cref="DebugBridgeProbeOutcome.Unknown"/>。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled; caller cancellation is propagated instead
    /// of being reported as unknown.
    /// <para><paramref name="cancellationToken"/> 被触发；调用方取消会被传播，而不是报告为未知。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="port"/> is outside the range [0, 65535].
    /// <para><paramref name="port"/> 不在 [0, 65535] 范围内。</para></exception>
    public static async Task<DebugBridgeProbeOutcome> TryProbeAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture));
        if (PositiveCache.TryGetValue(key, out var cached))
        {
            if (Environment.TickCount64 - cached.TimestampTicks < PositiveTtlMs)
            {
                ReportState(host, port, key, cached.Listening);
                return cached.Listening
                    ? DebugBridgeProbeOutcome.Listening
                    : DebugBridgeProbeOutcome.Refused;
            }
            PositiveCache.TryRemove(key, out _);
        }

        var (error, linkedCancellationRequested) = await ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (linkedCancellationRequested)
        {
            return DebugBridgeProbeOutcome.Unknown;
        }
        if (error == SocketError.ConnectionRefused)
        {
            ReportState(host, port, key, false);
            return DebugBridgeProbeOutcome.Refused;
        }
        if (error != SocketError.Success)
        {
            return DebugBridgeProbeOutcome.Unknown;
        }
        PositiveCache[key] = new ProbeResult(true, Environment.TickCount64);
        ReportState(host, port, key, true);
        return DebugBridgeProbeOutcome.Listening;
    }

    /// <summary>Connects once without translating failures: the raw socket error plus whether the
    /// linked 3-second deadline or caller token fired.
    /// <para>只建立一次连接且不翻译故障：返回原始 socket 错误，以及内部 3 秒截止时间或调用方令牌是否已触发。</para></summary>
    private static async Task<(SocketError Error, bool LinkedCancellationRequested)> ConnectAsync(
        string host, int port, CancellationToken cancellationToken)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var args = new SocketAsyncEventArgs
        {
            RemoteEndPoint = IPAddress.TryParse(host, out var address)
                ? new IPEndPoint(address, port) : new DnsEndPoint(host, port)
        };
        var completion = new TaskCompletionSource<SocketError>(TaskCreationOptions.RunContinuationsAsynchronously);
        args.Completed += (_, result) => completion.TrySetResult(result.SocketError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var cancellation = timeout.Token.Register(() => Socket.CancelConnectAsync(args));
        if (timeout.IsCancellationRequested)
        {
            return (SocketError.OperationAborted, true);
        }
        if (!socket.ConnectAsync(args))
        {
            completion.TrySetResult(args.SocketError);
        }
        if (timeout.IsCancellationRequested)
        {
            Socket.CancelConnectAsync(args);
        }
        var error = await completion.Task.ConfigureAwait(false);
        return (error, timeout.IsCancellationRequested);
    }

    /// <summary>
    /// Begins a probe scope: within the scope, probes of the same endpoint (including refusals)
    /// reuse the first result on the same thread and become invalid when the scope ends.
    /// Used to merge repeated endpoint connections within one full scan pass.
    /// <para>开启一次探测范围：范围内对同一端点的探测（含拒绝结果）在同一线程上复用首次结论，
    /// 范围结束即失效。用于一次全量扫描内合并对 adb/HDC 端点的重复连接。</para>
    /// </summary>
    /// <returns>Scope handle; Dispose restores the outer scope. <para>作用域句柄，Dispose 后恢复外层作用域。</para></returns>
    public static IDisposable BeginProbeScope()
    {
        _scopeCache ??= new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        _scopeDepth++;
        return new ProbeScope();
    }

    internal static bool ProbeEndpoint(string host, int port)
    {
        if (_scopeCache is not null && _scopeCache.TryGetValue(string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture)), out var scoped))
        {
            return scoped;
        }
        var result = IsListening(host, port);
        if (_scopeCache is not null)
        {
            _scopeCache[string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture))] = result;
        }
        return result;
    }

    /// <summary>Discards endpoint probe results after an explicit server lifecycle operation.
    /// <para>在显式的 server 生命周期操作之后丢弃该端点的探测结果。</para></summary>
    /// <param name="host">Server hostname or literal address exactly as passed to the probe.
    /// <para>与探测时传入完全一致的主机名或字面地址。</para></param>
    /// <param name="port">Server TCP port number. <para>server TCP 端口号。</para></param>
    public static void InvalidateProbe(string host, int port)
    {
        var key = string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture));
        PositiveCache.TryRemove(key, out _);
        _scopeCache?.TryRemove(key, out _);
    }

    /// <summary>
    /// Reads the process-global adb server availability without probing, honoring the fixed mode:
    /// <see cref="DebugBridgeRouteMode.DirectOnly"/> never selects the server, while the other modes
    /// read the snapshot.
    /// <para>读取全局 adb server 存在性参数；本方法不发起探测，并遵循固定模式：
    /// <see cref="DebugBridgeRouteMode.DirectOnly"/> 永不选择 server，其他模式读取快照。</para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the adb server route is selected for the default endpoint.
    /// <para>默认端点选择了 adb server 路由时为 <see langword="true"/>。</para></returns>
    public static bool UseAdbServer()
        => Mode == DebugBridgeRouteMode.DirectOnly ? false : Current.AdbServerAvailable;

    /// <summary>
    /// Selects the availability source per endpoint: the default endpoint reads the snapshot
    /// (and honors <see cref="DebugBridgeRouteMode.DirectOnly"/>); any other endpoint (test doubles
    /// or a second server) keeps live <see cref="ProbeEndpoint"/> probing.
    /// <para>按端点选择存在性来源：默认端点读取快照（并遵循
    /// <see cref="DebugBridgeRouteMode.DirectOnly"/>）；其他端点（测试替身或第二台 server）保持实时
    /// <see cref="ProbeEndpoint"/> 探测。</para>
    /// </summary>
    /// <param name="host">adb server host to decide for. <para>待判定的 adb server 主机。</para></param>
    /// <param name="port">adb server port to decide for. <para>待判定的 adb server 端口。</para></param>
    /// <returns>
    /// <see langword="true"/> when the endpoint is marked available.
    /// <para>端点被标记为可用时为 <see langword="true"/>。</para></returns>
    public static bool UseAdbServer(string host, int port)
        => host == DebugBridgeEndpoints.ServerHost && port == DebugBridgeEndpoints.AdbServerPort
            ? UseAdbServer()
            : Mode == DebugBridgeRouteMode.DirectOnly ? false : ProbeEndpoint(host, port);

    /// <summary>
    /// Selects the HDC availability source per endpoint: the default endpoint (including the port
    /// resolved from OHOS_HDC_SERVER_PORT) reads the snapshot and honors
    /// <see cref="DebugBridgeRouteMode.DirectOnly"/>; any other endpoint (test doubles or a custom
    /// server) keeps live <see cref="ProbeEndpoint"/> probing.
    /// <para>按端点选择 HDC 存在性来源：默认端点（含 OHOS_HDC_SERVER_PORT 解析出的端口）读取快照并遵循
    /// <see cref="DebugBridgeRouteMode.DirectOnly"/>；其他端点（测试替身或自定义 server）保持实时
    /// <see cref="ProbeEndpoint"/> 探测。</para>
    /// </summary>
    /// <param name="host">Server host, defaulting to <see cref="DebugBridgeEndpoints.ServerHost"/>.
    /// <para>server 主机，默认为 <see cref="DebugBridgeEndpoints.ServerHost"/>。</para></param>
    /// <param name="port">Explicit port number; when null the value of <see cref="DebugBridgeEndpoints.HdcServerPort"/> is used.
    /// <para>显式端口号；为 null 时使用 <see cref="DebugBridgeEndpoints.HdcServerPort"/> 的值。</para></param>
    /// <returns><see langword="true"/> when the endpoint is marked available.
    /// <para>端点被标记为可用时为 <see langword="true"/>。</para></returns>
    public static bool UseHdcServer(string host = DebugBridgeEndpoints.ServerHost, int? port = null)
    {
        var defaultPort = DebugBridgeEndpoints.HdcServerPort;
        var effectivePort = port ?? defaultPort;
        if (host != DebugBridgeEndpoints.ServerHost || effectivePort != defaultPort)
        {
            return Mode == DebugBridgeRouteMode.DirectOnly ? false : ProbeEndpoint(host, effectivePort);
        }

        return Mode == DebugBridgeRouteMode.DirectOnly ? false : Current.HdcServerAvailable;
    }

    /// <summary>
    /// Decides whether direct USB debug endpoints of one protocol are hidden from discovery under the
    /// fixed mode. Server-only hides them always; direct-only hides none; the other modes hide them
    /// while the snapshot marks that protocol's server as present.
    /// <para>按固定模式判定某协议的直连 USB 调试端点是否应从发现清单中隐藏。仅 server 模式始终隐藏；
    /// 仅直连模式不隐藏；其他模式在快照把该协议标记为 server 存在时隐藏。</para>
    /// </summary>
    /// <param name="protocol">Observed protocol tag, <c>adb</c> or <c>hdc</c>.
    /// <para>已观测的协议标记，<c>adb</c> 或 <c>hdc</c>。</para></param>
    /// <returns><see langword="true"/> when a discovered direct endpoint with this protocol must be suppressed.
    /// <para>该协议的已发现直连端点必须被抑制时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="protocol"/> is <see langword="null"/>.
    /// <para><paramref name="protocol"/> 为 <see langword="null"/>。</para></exception>
    public static bool SuppressDirectUsb(string protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var mode = Mode;
        var serverAvailable = protocol switch
        {
            "adb" => Current.AdbServerAvailable,
            "hdc" => Current.HdcServerAvailable,
            _ => false,
        };
        return mode switch
        {
            DebugBridgeRouteMode.ServerOnly => protocol is "adb" or "hdc",
            DebugBridgeRouteMode.DirectOnly => false,
            _ => serverAvailable && protocol is "adb" or "hdc",
        };
    }

    private sealed class ProbeScope : IDisposable
    {
        public void Dispose()
        {
            _scopeDepth--;
            if (_scopeDepth <= 0)
            {
                _scopeDepth = 0;
                _scopeCache = null;
            }
        }
    }
}

/// <summary>
/// Tri-state outcome of a debug bridge server endpoint probe. Only <see cref="Refused"/> means the
/// server is absent; <see cref="Unknown"/> means the probe could not conclude (deadline elapsed,
/// unreachable target, DNS failure, connect reset) and consumers must keep the last known state
/// instead of flipping to "no server".
/// <para>调试桥 server 端点探测的三态结果。只有 <see cref="Refused"/> 表示 server 不存在；
/// <see cref="Unknown"/> 表示探测未能得出结论（超时、不可达、DNS 失败、复位），
/// 消费方必须据此维持上次已知状态，不得翻转为「无 server」。</para>
/// </summary>
public enum DebugBridgeProbeOutcome
{
    /// <summary>The TCP connect completed: a server is listening on the endpoint.
    /// <para>TCP 连接成功建立：端点上有 server 正在监听。</para></summary>
    Listening,

    /// <summary>The connection was refused: the only outcome meaning the server is absent.
    /// <para>连接被拒绝：唯一表示 server 不存在的结果。</para></summary>
    Refused,

    /// <summary>The probe failed to conclude: deadline, unreachable host, DNS failure or reset;
    /// keep the previous state.
    /// <para>探测未能得出结论：截止时间到期、主机不可达、DNS 解析失败或连接复位；应保持上次状态。</para></summary>
    Unknown
}