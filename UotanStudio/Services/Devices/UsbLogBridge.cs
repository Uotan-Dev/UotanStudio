using System;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Services.Devices;

/// <summary>
/// <para>将 UotanStudio.Comm 的 USB 诊断日志桥接到统一日志服务（<see cref="LogService"/>）。</para>
/// Bridges UotanStudio.Comm USB diagnostics into the unified log service
/// (<see cref="LogService"/>).
/// </summary>
/// <remarks>
/// <para>仅当 <see cref="UsbTrace.IsEnabled"/> 开启（环境变量 FIRMWAREKIT_USB_DEBUG=1）时
/// Comm 层才会产生诊断输出，本桥本身不改变该开关。</para>
/// Comm only emits diagnostics while <see cref="UsbTrace.IsEnabled"/> is on (the
/// FIRMWAREKIT_USB_DEBUG=1 environment variable); installing this bridge does not
/// change that switch.
/// </remarks>
public sealed class UsbLogBridge : IUsbLogger
{
    /// <summary>
    /// <para>将诊断消息以 Trace 级别写入统一日志服务；<c>[ERROR]</c> 前缀的错误消息
    /// （来自 <see cref="UsbTrace.LogError"/> / <see cref="UsbTrace.LogErrorFormatted"/>）
    /// 以 Error 级别写入，确保异常原因在全局日志中醒目可见。</para>
    /// Writes a diagnostic message into the unified log service at Trace level; error
    /// messages prefixed with <c>[ERROR]</c> (from <see cref="UsbTrace.LogError"/> /
    /// <see cref="UsbTrace.LogErrorFormatted"/>) are written at Error level so exception
    /// reasons stand out in the global log.
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
    /// <para>将本桥安装为 Comm 层的 USB 诊断记录器。</para>
    /// Installs this bridge as the Comm-layer USB diagnostics logger.
    /// </summary>
    public static void Install() => UsbTrace.Logger = new UsbLogBridge();
}