namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Provides USB diagnostics logging and structured transfer events.
/// <para>提供 USB 诊断日志与结构化传输事件。</para>
/// </summary>
public static class UsbTrace
{
    /// <summary>
    /// Gets or sets whether plain text USB logs are enabled.
    /// <para>获取或设置是否启用纯文本 USB 日志。</para>
    /// </summary>
    public static bool IsEnabled { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("FIRMWAREKIT_USB_DEBUG"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Gets or sets the logger that receives plain text diagnostics.
    /// <para>获取或设置接收纯文本诊断日志的记录器。</para>
    /// </summary>
    public static IUsbLogger Logger { get; set; } = new ConsoleUsbLogger();

    /// <summary>
    /// Occurs when a structured transfer event is emitted.
    /// <para>当结构化传输事件产生时触发。</para>
    /// </summary>
    public static event Action<UsbTransferEvent>? TransferObserved;

    /// <summary>
    /// Gets whether any structured transfer subscriber is registered. Callers use this
    /// to skip allocating the <see cref="UsbTransferEvent"/> entirely when diagnostics
    /// are disabled (the hot transfer path otherwise constructs one per transfer).
    /// <para>获取是否注册了任何结构化传输订阅者。调用方据此在诊断关闭时完全跳过
    /// <see cref="UsbTransferEvent"/> 的分配（否则热传输路径每次传输都会构造一个）。</para>
    /// </summary>
    public static bool HasTransferSubscribers => TransferObserved != null;

    /// <summary>
    /// Gets or sets whether raw USB frames are captured into transfer events (opt-in).
    /// Enabled via the <c>FIRMWAREKIT_USB_CAPTURE_FRAMES=1</c> environment variable.
    /// <para>获取或设置是否将原始 USB 帧捕获到传输事件中（可选开启）。
    /// 通过 <c>FIRMWAREKIT_USB_CAPTURE_FRAMES=1</c> 环境变量启用。</para>
    /// </summary>
    public static bool CaptureFrames { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("FIRMWAREKIT_USB_CAPTURE_FRAMES"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Gets the maximum payload bytes captured per transfer event.
    /// <para>获取每个传输事件最多捕获的载荷字节数。</para>
    /// </summary>
    public const int MaxCaptureBytes = 256;

    /// <summary>
    /// Writes a plain text diagnostic message through <see cref="Logger"/>.
    /// <para>通过 <see cref="Logger"/> 写入纯文本诊断消息。</para>
    /// </summary>
    /// <param name="message">The diagnostic message. <para>诊断消息。</para></param>
    public static void Log(string message)
    {
        if (!IsEnabled || string.IsNullOrEmpty(message)) return;
        Logger.Log(message);
    }

    /// <summary>
    /// Writes an interpolated diagnostic message, deferring formatting until logging is
    /// confirmed enabled (avoids building the interpolation when diagnostics are off).
    /// <para>写入插值诊断消息，并在确认日志开启后才执行格式化
    /// （避免诊断关闭时仍构造插值字符串）。</para>
    /// </summary>
    /// <param name="message">The interpolated message. <para>插值消息。</para></param>
    public static void LogFormatted(FormattableString message)
    {
        if (!IsEnabled || message == null) return;
        Logger.Log(message.ToString());
    }

    /// <summary>
    /// Emits a structured transfer event.
    /// <para>发送结构化传输事件。</para>
    /// </summary>
    /// <param name="evt">The transfer event. <para>传输事件。</para></param>
    public static void EmitTransfer(UsbTransferEvent evt)
    {
        if (evt == null)
        {
            return;
        }

        Action<UsbTransferEvent>? handler = TransferObserved;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(evt);
        }
        catch (Exception ex)
        {
            Log($"TransferObserved callback failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
