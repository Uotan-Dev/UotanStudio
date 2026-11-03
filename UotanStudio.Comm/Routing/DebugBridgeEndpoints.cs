using System.Globalization;
using System.Net;

namespace UotanStudio.Comm.Routing;

/// <summary>
/// Debug bridge server endpoint facts shared by probing and session opening. The composition
/// root may forward these constants but every layer reads the single definition here.
/// <para>调试桥 server 端点事实，供探测与会话开启共用。组合根可以转发这些常量，但所有层都读取此处的唯一定义。</para>
/// </summary>
public static class DebugBridgeEndpoints
{
    /// <summary>Local debug bridge server host. <para>本机调试桥 server 主机。</para></summary>
    public const string ServerHost = "127.0.0.1";

    /// <summary>Upstream ANDROID_ADB_SERVER_PORT or a loopback ADB_SERVER_SOCKET override, otherwise 5037.
    /// <para>遵循上游 ANDROID_ADB_SERVER_PORT 或回环 ADB_SERVER_SOCKET 覆盖，否则使用 5037。</para></summary>
    /// <remarks>
    /// ADB_SERVER_SOCKET wins over ANDROID_ADB_SERVER_PORT, exactly like the adb client: <c>tcp:host:port</c>
    /// is honored only when the host is absent or loopback, because this process serves a local adb server;
    /// a remote socket spec, a non-tcp spec or a malformed port is ignored instead of redirecting the channel.
    /// The environment variables are read once per process and the result is cached, so implicit callers
    /// cannot observe a value change mid-scan.
    /// <para>ADB_SERVER_SOCKET 优先于 ANDROID_ADB_SERVER_PORT，与 adb 客户端一致：仅在主机缺失或为回环时
    /// 采用 <c>tcp:host:port</c> 的端口，因为本进程只服务本机 adb server；远端套接字、非 tcp 规范或畸形端口
    /// 会被忽略，而不是改道。环境变量每个进程只读取一次并缓存结果，隐式调用方不会在一次扫描中观测到值变化。</para>
    /// </remarks>
    /// <value>The adb server port in [1, 65535]: the socket-spec or port override when it parses as a
    /// decimal port within that range, otherwise 5037.
    /// <para>取值范围为 [1, 65535] 的 adb server 端口：套接字规范或端口覆盖可解析为该范围内的十进制端口时
    /// 使用它，否则为 5037。</para></value>
    public static int AdbServerPort
    {
        get
        {
            lock (AdbPortLock)
            {
                _adbServerPort ??= ResolveAdbServerPort(
                    Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET"),
                    Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"));
                return _adbServerPort.Value;
            }
        }
    }

    private const int DefaultAdbServerPort = 5037;

    private static readonly Lock AdbPortLock = new();

    private static int? _adbServerPort;

    /// <summary>
    /// Resolves the local adb server port from the upstream overrides so the precedence is testable without
    /// mutating process-wide environment variables.
    /// <para>按上游覆盖解析本机 adb server 端口，使优先级无需修改进程级环境变量即可测试。</para>
    /// </summary>
    /// <param name="socketSpec">The <c>ADB_SERVER_SOCKET</c> value, or <see langword="null"/>.
    /// <para><c>ADB_SERVER_SOCKET</c> 的取值；可为 <see langword="null"/>。</para></param>
    /// <param name="portOverride">The <c>ANDROID_ADB_SERVER_PORT</c> value, or <see langword="null"/>.
    /// <para><c>ANDROID_ADB_SERVER_PORT</c> 的取值；可为 <see langword="null"/>。</para></param>
    /// <returns>The resolved port, defaulting to 5037 when neither value names a usable local port.
    /// <para>解析出的端口；两个取值都没有给出可用的本机端口时为 5037。</para></returns>
    internal static int ResolveAdbServerPort(string? socketSpec, string? portOverride)
        => TryReadLoopbackTcpPort(socketSpec, out var socketPort)
            ? socketPort
            : TryParsePort(portOverride) ?? DefaultAdbServerPort;

    /// <summary>
    /// Reads the port out of an adb socket spec, accepting it only when the spec targets this machine:
    /// <c>tcp:port</c>, <c>tcp:host:port</c> and <c>tcp:[v6]:port</c> all qualify as long as the host is
    /// absent or loopback.
    /// <para>从 adb 套接字规范中读取端口，只在规范指向本机时才接受：<c>tcp:port</c>、<c>tcp:host:port</c>
    /// 与 <c>tcp:[v6]:port</c> 只要主机缺失或为回环地址即可。</para>
    /// </summary>
    /// <param name="spec">The socket spec to read. <para>待读取的套接字规范。</para></param>
    /// <param name="port">The parsed port when the spec qualifies. <para>规范符合条件时解析出的端口。</para></param>
    /// <returns><see langword="true"/> when a loopback tcp spec carried a usable port.
    /// <para>回环 tcp 规范带有可用端口时为 <see langword="true"/>。</para></returns>
    private static bool TryReadLoopbackTcpPort(string? spec, out int port)
    {
        port = 0;
        const string prefix = "tcp:";
        if (spec is null || !spec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = spec[prefix.Length..];
        string host;
        string portText;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0 || close + 1 >= rest.Length || rest[close + 1] != ':')
            {
                return false;
            }

            host = rest[1..close];
            portText = rest[(close + 2)..];
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            host = colon >= 0 ? rest[..colon] : string.Empty;
            portText = colon >= 0 ? rest[(colon + 1)..] : rest;
        }

        if (host.Length > 0 &&
            !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !(IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)))
        {
            return false;
        }

        if (TryParsePort(portText) is not { } value)
        {
            return false;
        }

        port = value;
        return true;
    }

    private static int? TryParsePort(string? raw)
        => !string.IsNullOrEmpty(raw) &&
            !raw.Any(c => !char.IsAsciiDigit(c)) &&
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            value is >= 1 and <= 65535
            ? value
            : null;

    /// <summary>Upstream OHOS_HDC_SERVER_PORT override, otherwise 8710.
    /// <para>遵循上游 OHOS_HDC_SERVER_PORT 环境变量，否则使用 8710。</para></summary>
    /// <remarks>
    /// The environment variable is read once per process and the result is cached, so implicit
    /// callers cannot observe a value change mid-scan and a malformed override cannot throw from
    /// the four implicit call sites; an invalid or out-of-range value falls back to the default
    /// port instead.
    /// <para>环境变量每个进程只读取一次并缓存结果，隐式调用方不会在一次扫描过程中观测到值变化，
    /// 畸形覆盖值也不会在四个隐式调用点抛出异常；非法或超出范围的值回退到默认端口。</para>
    /// </remarks>
    /// <value>The HDC server port in [1, 65535]: the cached <c>OHOS_HDC_SERVER_PORT</c> value when it
    /// parses as a decimal port within that range, otherwise 8710.
    /// <para>取值范围为 [1, 65535] 的 HDC server 端口：缓存的 <c>OHOS_HDC_SERVER_PORT</c> 值在其可
    /// 解析为该范围内的十进制端口时使用它，否则为 8710。</para></value>
    public static int HdcServerPort
    {
        get
        {
            lock (HdcPortLock)
            {
                _hdcServerPort ??= ResolveHdcServerPort(Environment.GetEnvironmentVariable("OHOS_HDC_SERVER_PORT"));
                return _hdcServerPort.Value;
            }
        }
    }

    private const int DefaultHdcServerPort = 8710;

    private static readonly Lock HdcPortLock = new();

    private static int? _hdcServerPort;

    /// <summary>
    /// Clears the cached HDC port so the next read re-evaluates the environment override; reserved
    /// for tests that change <c>OHOS_HDC_SERVER_PORT</c> within one process.
    /// <para>清除缓存的 HDC 端口，使下次读取重新按环境变量求值；仅供在单一进程内修改
    /// <c>OHOS_HDC_SERVER_PORT</c> 的测试使用。</para>
    /// </summary>
    internal static void ResetHdcServerPortCache()
    {
        lock (HdcPortLock)
        {
            _hdcServerPort = null;
        }
    }

    private static int ResolveHdcServerPort(string? rawValue)
        => TryParsePort(rawValue) ?? DefaultHdcServerPort;
}