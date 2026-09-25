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
    public static int HdcServerPort
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("OHOS_HDC_SERVER_PORT");
            if (string.IsNullOrEmpty(value)) return 8710;
            if (value.Any(c => !char.IsAsciiDigit(c)) || !int.TryParse(value, CultureInfo.InvariantCulture, out var result) || result is < 1 or > 65535)
                throw new InvalidOperationException("OHOS_HDC_SERVER_PORT must be a decimal port in [1, 65535].");
            return result;
        }
    }
}