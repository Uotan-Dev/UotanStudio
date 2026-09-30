using System.Globalization;

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

    /// <summary>Local ADB server port. <para>本机 adb server 端口。</para></summary>
    public const int AdbServerPort = 5037;

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
    {
        if (string.IsNullOrEmpty(rawValue)) return DefaultHdcServerPort;
        if (rawValue.Any(c => !char.IsAsciiDigit(c)) || !int.TryParse(rawValue, CultureInfo.InvariantCulture, out var result) || result is < 1 or > 65535)
            return DefaultHdcServerPort;
        return result;
    }
}