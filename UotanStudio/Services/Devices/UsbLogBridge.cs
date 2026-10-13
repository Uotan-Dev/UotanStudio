using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Routes USB diagnostics into the unified log service.
/// <para>将 USB 诊断输出汇入统一日志服务。</para>
/// </summary>
public sealed class UsbLogBridge : IUsbLogger
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
    /// Installs this bridge as the Comm-layer USB diagnostics logger.
    /// <para>将本桥安装为 Comm 层的 USB 诊断记录器。</para>
    /// </summary>
    public static void Install()
    {
        lock (InstallLock)
        {
            UsbTrace.Logger = new UsbLogBridge();
            DebugBridgeRoute.ServerAvailabilityChanged -= LogServerAvailability;
            DebugBridgeRoute.ServerAvailabilityChanged += LogServerAvailability;
        }
    }

    private static readonly Lock InstallLock = new();

    internal static void LogServerAvailability(string host, int port, bool listening)
    {
        var message = listening
            ? $"Debug bridge server endpoint {host}:{port} is listening; device operations use the server."
            : $"Debug bridge server connection refused at {host}:{port}; server is absent, using direct USB for available USB targets.";
        LogService.Instance.Write(LogLevel.Info, "DeviceCommunication", message);
    }
}