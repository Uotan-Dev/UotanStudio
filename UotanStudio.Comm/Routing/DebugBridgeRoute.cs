using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Routing;

/// <summary>Probes debug bridge server liveness over plain TCP and holds the process-global adb and
/// HDC server availability parameters. Only a refused connection means absent.
/// <para>以普通 TCP 探测调试桥 server 存活，并持有进程全局的 adb 与 HDC server 存在性参数。仅连接被拒绝表示不存在。</para></summary>
/// <remarks>
/// <para>正值结果（server 存在）按短 TTL 缓存，避免一次扫描内对同一端点重复建连；
/// 「连接被拒绝」结果不缓存（保持既有约定：仅连接被拒绝视为不存在，不缓存负结果），
/// 但同一调用方在同一次扫描流程内可通过 <see cref="BeginProbeScope"/> 复用一次探测结论。
/// server 归属判定与路由开启由上层 <c>Devices.Services.DebugBridgeSessionOpener</c> 提供，
/// 本类型只回答「端口上是否有 server 在监听」。需要区分「拒绝」与「无法得出结论」的调用方
/// 使用 <see cref="TryProbeAsync"/>，未知结果必须保持上次状态。</para>
/// <para>Positive results (server present) are cached for a short TTL so a single scan does not
/// repeatedly connect to the same endpoint; a refused connection is never cached (existing
/// contract: only a refused connection means absent, negative results are not cached). Callers
/// running one scan pass can share a single probe via <see cref="BeginProbeScope"/>. Server
/// ownership decisions and routed session opening live in the upper
/// <c>Devices.Services.DebugBridgeSessionOpener</c>; this type only answers whether a server
/// is listening on an endpoint. Callers that must distinguish a refusal from an inconclusive
/// probe use <see cref="TryProbeAsync"/>, where an unknown outcome keeps the last known state.</para>
/// </remarks>
public static class DebugBridgeRoute
{
    private const int PositiveTtlMs = 2000;
    private static readonly Lock StateLock = new();
    private static readonly Dictionary<string, bool> ReportedStates = new(StringComparer.Ordinal);

    private static bool _adbServerAvailable;

    /// <summary>
    /// Process-global adb server availability, written at startup and refreshed each pass by the conclusive probe of the default endpoint; inconclusive probes keep the previous value. Every adb route reads it to choose between server forwarding and direct connection.
    /// <para>进程级的 adb server 可用性，启动时写入，并由对默认端点的确证探测每轮刷新；未确证的探测保持原值。所有 adb 路由据此在“经 server 转发”与“直连”之间选择。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the last conclusive probe of <see cref="DebugBridgeEndpoints.ServerHost"/>
    /// at <see cref="DebugBridgeEndpoints.AdbServerPort"/> found a listening server.
    /// <para>当对默认端点的最后一次确定性探测发现 server 正在监听时为 <see langword="true"/>。</para></value>
    public static bool AdbServerAvailable => Volatile.Read(ref _adbServerAvailable);

    internal static void SetAdbServerAvailable(bool available) => Volatile.Write(ref _adbServerAvailable, available);

    private static bool _hdcServerAvailable;

    /// <summary>
    /// Process-global HDC server availability, mirroring <see cref="AdbServerAvailable"/>: written
    /// by <see cref="InitializeHdcServerProbe"/> at startup and refreshed each pass by the
    /// server-lifecycle watch's conclusive probe of the default HDC endpoint; inconclusive probes
    /// keep the previous value. Every HDC operation route (shell, hilog, file transfer) reads this
    /// <para>进程全局的 HDC server 存在性参数，语义与 <see cref="AdbServerAvailable"/> 对称：启动时由
    /// <see cref="InitializeHdcServerProbe"/> 探测写入，之后由 server 监视循环每轮的确定性结论刷新；
    /// 未知结果保持上次状态。所有 HDC 操作路由（shell、hilog、文件传输等）读取本参数决定经
    /// HDC server 转发还是直连 USB。</para>
    /// value to choose server forwarding versus direct USB access.</summary>
    /// <value>
    /// <see langword="true"/> when the last conclusive probe of <see cref="DebugBridgeEndpoints.ServerHost"/>
    /// at <see cref="DebugBridgeEndpoints.HdcServerPort"/> found a listening server.
    /// <para>当对默认 HDC 端点的最后一次确定性探测发现 server 正在监听时为 <see langword="true"/>。</para></value>
    public static bool HdcServerAvailable => Volatile.Read(ref _hdcServerAvailable);

    internal static void SetHdcServerAvailable(bool available) => Volatile.Write(ref _hdcServerAvailable, available);

    /// <summary>
    /// Startup and post-lifecycle resync boundary: probes the default HDC endpoint once for real
    /// and writes the conclusive outcome (listening or refused) into
    /// <see cref="HdcServerAvailable"/>; an inconclusive probe keeps the current value. The method
    /// <para>程序启动与显式 server 生命周期操作后的重同步边界：对默认 HDC 端点做一次真实探测，
    /// 把确定性结论（监听或拒绝）写入 <see cref="HdcServerAvailable"/>；未知结论保持当前值。
    /// 本方法不抛出 socket 异常。</para>
    /// never throws socket exceptions.</summary>
    public static void InitializeHdcServerProbe()
    {
        try
        {
            var outcome = TryProbeAsync(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.HdcServerPort)
                .GetAwaiter().GetResult();
            if (outcome != DebugBridgeProbeOutcome.Unknown)
            {
                SetHdcServerAvailable(outcome == DebugBridgeProbeOutcome.Listening);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Startup and post-lifecycle resync boundary: probes the default adb endpoint once for real
    /// and writes the conclusive outcome (listening or refused) into
    /// <see cref="AdbServerAvailable"/>; an inconclusive probe keeps the current value. The method
    /// <para>程序启动与显式 server 生命周期操作后的重同步边界：对默认 adb 端点做一次真实探测，
    /// 把确定性结论（监听或拒绝）写入 <see cref="AdbServerAvailable"/>；未知结论保持当前值。
    /// 本方法不抛出 socket 异常。</para>
    /// never throws socket exceptions.</summary>
    public static void InitializeAdbServerProbe()
    {
        try
        {
            var outcome = TryProbeAsync(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.AdbServerPort)
                .GetAwaiter().GetResult();
            if (outcome != DebugBridgeProbeOutcome.Unknown)
            {
                SetAdbServerAvailable(outcome == DebugBridgeProbeOutcome.Listening);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

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
    /// <remarks>
    /// <para>存在结果经 2 秒 TTL 缓存；拒绝与其他错误每次都真实探测。</para>
    /// <para>Present results go through a 2-second TTL cache; refusals and other errors probe for real every time.</para>
    /// </remarks>
    /// <param name="host">Server hostname or literal address; a non-parseable value is resolved as DNS.
    /// <para>server 主机名或字面地址；无法解析的值将按 DNS 解析。</para></param>
    /// <param name="port">Server TCP port number. <para>server TCP 端口号。</para></param>
    /// <returns>
    /// <see langword="true"/> only when the TCP connect completes successfully; <see langword="false"/>
    /// only when the connection is refused.
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
    /// Reads the process-global adb server availability parameter without probing. The value is
    /// written by <see cref="InitializeAdbServerProbe"/> at startup and kept fresh by the
    /// server-watch loop's periodic <see cref="TryProbeAsync"/> passes over the default
    /// <para>读取全局 adb server 存在性参数；本方法不发起探测。参数由启动时的
    /// <see cref="InitializeAdbServerProbe"/> 写入，并由 server 监视循环对默认端点的周期性
    /// <see cref="TryProbeAsync"/> 保持更新。</para>
    /// endpoint.</summary>
    /// <returns>
    /// <see langword="true"/> when the global parameter currently marks the adb server as present.
    /// <para>当全局参数当前标记 adb server 存在时返回 <see langword="true"/>。</para></returns>
    public static bool UseAdbServer() => AdbServerAvailable;

    /// <summary>
    /// Selects the availability source per endpoint: the default endpoint reads the global
    /// <see cref="AdbServerAvailable"/> parameter; any other endpoint (test doubles or a second
    /// <para>按端点选择存在性来源：默认端点读取全局参数 <see cref="AdbServerAvailable"/>；
    /// 其他端点（测试替身或第二台 server）保持实时 <see cref="ProbeEndpoint"/> 探测。</para>
    /// server) keeps live <see cref="ProbeEndpoint"/> probing.</summary>
    /// <param name="host">adb server host to decide for. <para>待判定的 adb server 主机。</para></param>
    /// <param name="port">adb server port to decide for. <para>待判定的 adb server 端口。</para></param>
    /// <returns>
    /// <see langword="true"/> when the endpoint is marked available, from the global parameter for
    /// the default endpoint or from a probe for any other endpoint.
    /// <para>端点被标记为可用时为 <see langword="true"/>；默认端点取全局参数，其他端点取探测结果。</para></returns>
    public static bool UseAdbServer(string host, int port)
        => host == DebugBridgeEndpoints.ServerHost && port == DebugBridgeEndpoints.AdbServerPort
            ? AdbServerAvailable
            : ProbeEndpoint(host, port);

    /// <summary>
    /// Selects the HDC availability source per endpoint: the default endpoint (including the port
    /// resolved from OHOS_HDC_SERVER_PORT) reads the global <see cref="HdcServerAvailable"/>
    /// parameter; any other endpoint (test doubles or a custom server) keeps live
    /// <para>按端点选择 HDC 存在性来源：默认端点（含 OHOS_HDC_SERVER_PORT 解析出的端口）读取全局参数
    /// <see cref="HdcServerAvailable"/>；其他端点（测试替身或自定义 server）保持实时
    /// <see cref="ProbeEndpoint"/> 探测。</para>
    /// <see cref="ProbeEndpoint"/> probing.</summary>
    /// <param name="host">Server host, defaulting to <see cref="DebugBridgeEndpoints.ServerHost"/>.
    /// <para>server 主机，默认为 <see cref="DebugBridgeEndpoints.ServerHost"/>。</para></param>
    /// <param name="port">Explicit port number; when null the value of
    /// <see cref="DebugBridgeEndpoints.HdcServerPort"/> is used.
    /// <para>显式端口号；为 null 时使用 <see cref="DebugBridgeEndpoints.HdcServerPort"/> 的值。</para></param>
    /// <returns>
    /// <see langword="true"/> when the endpoint is marked available, from the global parameter for
    /// the default endpoint or from a probe for any other endpoint.
    /// <para>端点被标记为可用时为 <see langword="true"/>；默认端点取全局参数，其他端点取探测结果。</para></returns>
    public static bool UseHdcServer(string host = DebugBridgeEndpoints.ServerHost, int? port = null)
    {
        var defaultPort = DebugBridgeEndpoints.HdcServerPort;
        var effectivePort = port ?? defaultPort;
        return host == DebugBridgeEndpoints.ServerHost && effectivePort == defaultPort
            ? HdcServerAvailable
            : ProbeEndpoint(host, effectivePort);
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