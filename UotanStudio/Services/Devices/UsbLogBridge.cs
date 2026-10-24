using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Routes USB diagnostics into the unified log service.
/// <para>将 USB 诊断输出汇入统一日志服务。</para>
/// </summary>
public sealed class UsbLogBridge : IUsbLogger, ICommLogger
{
    /// <summary>
    /// Writes a diagnostic message into the unified log service at Trace level; error
    /// messages prefixed with <c>[ERROR]</c> (from <see cref="UsbTrace.LogError"/> /
    /// <see cref="UsbTrace.LogErrorFormatted"/>) are written at Error level so exception
    /// reasons stand out in the global log.
    /// <para>将诊断消息以 Trace 级别写入统一日志服务；<c>[ERROR]</c> 前缀的错误消息
    /// （来自 <see cref="UsbTrace.LogError"/> / <see cref="UsbTrace.LogErrorFormatted"/>）
    /// 以 Error 级别写入，确保异常原因在全局日志中醒目可见。</para>
    /// </summary>
    /// <param name="message">The diagnostic message. <para>诊断日志消息内容。</para></param>
    public void Log(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var level = message.StartsWith("[ERROR]", StringComparison.Ordinal)
            ? LogLevel.Error
            : LogLevel.Trace;
        LogService.Instance.Write(level, "USB", message);
    }

    /// <summary>
    /// Writes one Comm diagnostic into the unified log service, mapping its level onto the host log level and
    /// keeping the Comm category as the log source.
    /// <para>将一条 Comm 诊断写入统一日志服务：把其级别映射到宿主日志级别，并保留 Comm 分类作为日志来源。</para>
    /// </summary>
    /// <param name="level">Comm message severity. <para>Comm 消息严重级别。</para></param>
    /// <param name="category">Stable Comm category such as <c>adb-sync</c>.
    /// <para>稳定的 Comm 分类，例如 <c>adb-sync</c>。</para></param>
    /// <param name="message">Rendered message text. <para>已渲染的消息文本。</para></param>
    public void Log(CommLogLevel level, string category, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var hostLevel = level switch
        {
            CommLogLevel.Error => LogLevel.Error,
            CommLogLevel.Info => LogLevel.Info,
            CommLogLevel.Debug => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
        LogService.Instance.Write(hostLevel, string.IsNullOrWhiteSpace(category) ? "Comm" : category, message);
    }

    /// <summary>
    /// Installs this bridge as the Comm-layer USB and Comm diagnostics logger.
    /// <para>将本桥安装为 Comm 层的 USB 与 Comm 诊断记录器。</para>
    /// </summary>
    public static void Install()
    {
        lock (InstallLock)
        {
            var installed = new UsbLogBridge();
            UsbTrace.Logger = installed;
            CommTrace.Logger = installed;
            DebugBridgeRoute.ServerAvailabilityChanged -= LogServerAvailability;
            DebugBridgeRoute.ServerAvailabilityChanged += LogServerAvailability;
        }
    }

    private static readonly Lock InstallLock = new();

    internal static void LogServerAvailability(string host, int port, bool listening)
    {
        var mode = DebugBridgeRoute.Mode;
        var selection = mode switch
        {
            DebugBridgeRouteMode.ServerOnly => "server-only routing",
            DebugBridgeRouteMode.DirectOnly => "direct-only routing",
            DebugBridgeRouteMode.StartupFrozen => "frozen startup routing",
            _ => listening ? "server routing" : "direct routing for unowned targets",
        };
        var message = listening
            ? $"Debug bridge server endpoint {host}:{port} is listening; current selection is {selection}."
            : $"Debug bridge server connection refused at {host}:{port}; server is absent; current selection is {selection}.";
        LogService.Instance.Write(LogLevel.Info, "DeviceCommunication", message);
    }
}