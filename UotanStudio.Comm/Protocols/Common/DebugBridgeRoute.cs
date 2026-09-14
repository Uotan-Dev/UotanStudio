using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Comm.Protocols.Common;

/// <summary>Resolves debug bridge ownership before any USB handle is opened. Only a refused connection means absent.</summary>
/// <remarks>
/// <para>正值结果（server 存在）按短 TTL 缓存，避免一次扫描内对同一端点重复建连；
/// 「连接被拒绝」结果不缓存（保持既有约定：仅连接被拒绝视为不存在，不缓存负结果），
/// 但同一调用方在同一次扫描流程内可通过 <see cref="BeginProbeScope"/> 复用一次探测结论。</para>
/// <para>Positive results (server present) are cached for a short TTL so a single scan does not
/// repeatedly connect to the same endpoint; a refused connection is never cached (existing
/// contract: only a refused connection means absent, negative results are not cached). Callers
/// running one scan pass can share a single probe via <see cref="BeginProbeScope"/>.</para>
/// </remarks>
public static class DebugBridgeRoute
{
    private const int PositiveTtlMs = 2000;

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
    {
        var key = string.Concat(host, ":", port.ToString(CultureInfo.InvariantCulture));
        if (PositiveCache.TryGetValue(key, out var cached))
        {
            if (Environment.TickCount64 - cached.TimestampTicks < PositiveTtlMs)
            {
                return cached.Listening;
            }
            PositiveCache.TryRemove(key, out _);
        }

        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            PositiveCache[key] = new ProbeResult(true, Environment.TickCount64);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return false; }
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

    private static bool Probe(string host, int port)
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

    /// <summary>Chooses a server even when the requested target is missing or unauthorized; those errors must not open USB.</summary>
    public static bool UseAdbServer() => Probe("127.0.0.1", 5037);

    /// <summary>Checks the configured HDC endpoint on every new operation.</summary>
    public static bool UseHdcServer(string host = "127.0.0.1", int? port = null)
        => Probe(host, port ?? HdcServerClient.DefaultServerPort);

    /// <summary>Rejects raw debug USB access while a server owns the protocol.</summary>
    public static void RequireDirectUsb(byte? interfaceClass, byte? subclass, byte? protocol)
    {
        if (interfaceClass != 0xff || protocol != 1) return;
        bool present = subclass switch { 0x42 => UseAdbServer(), 0x50 => UseHdcServer(), _ => false };
        if (present) throw new IOException("A debug bridge server is running; use its target service instead of opening USB.");
    }

    /// <summary>Creates a routed ADB session; the direct factory is invoked only when the server is absent.</summary>
    public static AdbSession OpenAdb(string serial, Func<AdbSession> openDirect,
        string host = "127.0.0.1", int port = 5037)
        => Probe(host, port) ? new AdbSession(new AdbServerServices(serial, host, port)) : openDirect();

    /// <summary>Creates a routed HDC session without relying on a discovery key prefix.</summary>
    public static HdcSession OpenHdc(string serial, Func<HdcSession> openDirect,
        string host = "127.0.0.1", int? port = null)
    {
        if (!UseHdcServer(host, port)) return openDirect();
        using var server = new HdcServerClient(host, port);
        return server.CreateSession(serial);
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