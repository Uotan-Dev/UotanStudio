#if DEBUG
using Microsoft.Extensions.Logging;

namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Debug-only bridge that forwards the library's logging and structured transfer
/// events to Microsoft.Extensions.Logging. Compiled only in Debug builds, so the
/// Release library keeps its zero-dependency logging abstraction.
/// <para>仅在 Debug 构建中编译的桥接器，将库的日志与结构化传输事件转发到
/// Microsoft.Extensions.Logging。Release 库保持零依赖日志抽象。</para>
/// </summary>
public static class UsbLoggerBridge
{
    private static bool _attached;

    /// <summary>
    /// Attaches an <see cref="ILogger"/> to receive plain-text logs and structured
    /// transfer events. Idempotent; subsequent calls are ignored.
    /// <para>接入 <see cref="ILogger"/> 以接收明文日志与结构化传输事件。幂等；重复调用被忽略。</para>
    /// </summary>
    /// <param name="logger">The logger to forward to. <para>用于转发的记录器。</para></param>
    /// <remarks>
    /// Structured transfer events always flow after attaching. Plain-text logs are
    /// additionally gated by <see cref="UsbTrace.IsEnabled"/>
    /// (<c>FIRMWAREKIT_USB_DEBUG=1</c>) or can be forced via <see cref="UsbTrace.IsEnabled"/>.
    /// <para>接入后结构化传输事件始终流转；明文日志还受 <see cref="UsbTrace.IsEnabled"/>
    /// （<c>FIRMWAREKIT_USB_DEBUG=1</c>）门控，也可直接设置 <see cref="UsbTrace.IsEnabled"/>。</para>
    /// </remarks>
    public static void Attach(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (_attached) return;
        _attached = true;

        UsbTrace.Logger = new LoggerAdapter(logger);
        UsbTrace.TransferObserved += evt =>
            logger.Log(LogLevel.Debug,
                "USB transfer {Backend} {Operation} {RequestedBytes}/{TransferredBytes} bytes {TimeoutMs}ms retries={RetryCount} err={NativeErrorCode} {ElapsedMs}ms {Outcome}",
                evt.Backend, evt.Operation, evt.RequestedBytes, evt.TransferredBytes,
                evt.TimeoutMs, evt.RetryCount, evt.NativeErrorCode, evt.ElapsedMs, evt.Outcome);
    }

    private sealed class LoggerAdapter(ILogger logger) : IUsbLogger
    {
        private readonly ILogger _logger = logger;

        public void Log(string message) => _logger.Log(LogLevel.Debug, "{Message}", message);
    }
}
#endif
