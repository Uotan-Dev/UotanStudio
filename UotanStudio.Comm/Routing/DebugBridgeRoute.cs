using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Routing;

/// <summary>Probes debug bridge server liveness over plain TCP. Only a refused connection means absent.</summary>
/// <remarks>
/// <para>正值结果（server 存在）按短 TTL 缓存，避免一次扫描内对同一端点重复建连；
/// 「连接被拒绝」结果不缓存（保持既有约定：仅连接被拒绝视为不存在，不缓存负结果），
/// 但同一调用方在同一次扫描流程内可通过 <see cref="BeginProbeScope"/> 复用一次探测结论。
/// server 归属判定与路由开启由上层 <c>Devices.Services.DebugBridgeSessionOpener</c> 提供，
/// 本类型只回答「端口上是否有 server 在监听」。</para>
/// <para>Positive results (server present) are cached for a short TTL so a single scan does not
/// repeatedly connect to the same endpoint; a refused connection is never cached (existing
/// contract: only a refused connection means absent, negative results are not cached). Callers
/// running one scan pass can share a single probe via <see cref="BeginProbeScope"/>. Server
/// ownership decisions and routed session opening live in the upper
/// <c>Devices.Services.DebugBridgeSessionOpener</c>; this type only answers whether a server
/// is listening on an endpoint.</para>
/// </remarks>
public static class DebugBridgeRoute
{
    private const int PositiveTtlMs = 2000;
    private static readonly Lock StateLock = new();
    private static readonly Dictionary<string, bool> ReportedStates = new(StringComparer.Ordinal);

    /// <summary>Reports initial endpoint state and subsequent transitions for host logging; never writes to the console.</summary>
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
            // Diagnostic subscribers must not change transport ownership decisions.
        }
    }

    private sealed record ProbeResult(bool Listening, long TimestampTicks);

    private static readonly ConcurrentDictionary<string, ProbeResult> PositiveCache = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static ConcurrentDictionary<string, bool>? _scopeCache;

    [ThreadStatic]
    private static int _scopeDepth;

    /// <summary>Checks an endpoint without caching absence or converting protocol failures into USB permission.</summary>
    /// <remarks>
    /// <para>存在结果经 2 秒 TTL 缓存；拒绝与其他错误每次都真实探测。</para>
    /// <para>Present results go through a 2-second TTL cache; refusals and other errors probe for real every time.</para>
    /// </remarks>
    public static bool IsListening(string host, int port)
        => IsListeningAsync(host, port).GetAwaiter().GetResult();

    /// <summary>Asynchronously probes endpoint ownership without treating timeout or protocol failure as absence.</summary>
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

        // SocketAsyncEventArgs exposes refusal as a result rather than a thrown exception,
        // avoiding debugger first-chance noise during ordinary server discovery.
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
        timeout.Token.ThrowIfCancellationRequested();
        if (!socket.ConnectAsync(args))
        {
            completion.TrySetResult(args.SocketError);
        }
        // Cancellation can precede submission; cancel again after submission to close that race.
        if (timeout.IsCancellationRequested)
        {
            Socket.CancelConnectAsync(args);
        }
        var error = await completion.Task.ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
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
    /// <para>开启一次探测范围：范围内对同一端点的探测（含拒绝结果）在同一线程上复用首次结论，
    /// 范围结束即失效。用于一次全量扫描内合并对 adb/HDC 端点的重复连接。</para>
    /// Begins a probe scope: within the scope, probes of the same endpoint (including refusals)
    /// reuse the first result on the same thread and become invalid when the scope ends.
    /// Used to merge repeated endpoint connections within one full scan pass.
    /// </summary>
    /// <returns><para>作用域句柄，Dispose 后恢复外层作用域。</para>Scope handle; Dispose restores the outer scope.</returns>
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

    /// <summary>Discards endpoint probe results after an explicit server lifecycle operation.</summary>
    public static void InvalidateProbe(string host, int port)
    {
        var key = string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture));
        PositiveCache.TryRemove(key, out _);
        _scopeCache?.TryRemove(key, out _);
    }

    /// <summary>Reports whether the local ADB endpoint currently has a listening server.</summary>
    public static bool UseAdbServer() => ProbeEndpoint(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.AdbServerPort);

    /// <summary>Checks the configured HDC endpoint on every new operation.</summary>
    public static bool UseHdcServer(string host = DebugBridgeEndpoints.ServerHost, int? port = null)
        => ProbeEndpoint(host, port ?? DebugBridgeEndpoints.HdcServerPort);

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