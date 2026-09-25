namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Describes the kind of transfer operation.
/// <para>描述传输操作类型。</para>
/// </summary>
public enum UsbTransferOperation
{
    /// <summary>
    /// Read operation.
    /// <para>读取操作。</para>
    /// </summary>
    Read = 0,

    /// <summary>
    /// Write operation.
    /// <para>写入操作。</para>
    /// </summary>
    Write = 1,

    /// <summary>
    /// Reset operation.
    /// <para>重置操作。</para>
    /// </summary>
    Reset = 2,

    /// <summary>
    /// Device enumeration operation.
    /// <para>设备枚举操作。</para>
    /// </summary>
    Enumerate = 3
}

/// <summary>
/// Describes how a transfer operation finished.
/// <para>描述传输操作结束状态。</para>
/// </summary>
public enum UsbTransferOutcome
{
    /// <summary>
    /// Transfer completed successfully.
    /// <para>传输成功完成。</para>
    /// </summary>
    Success = 0,

    /// <summary>
    /// Transfer stopped due to timeout.
    /// <para>传输因超时结束。</para>
    /// </summary>
    Timeout = 1,

    /// <summary>
    /// Transfer completed with fewer bytes than requested.
    /// <para>传输字节数少于请求值。</para>
    /// </summary>
    ShortTransfer = 2,

    /// <summary>
    /// Transfer failed with a fatal error.
    /// <para>传输遇到致命错误。</para>
    /// </summary>
    FatalError = 3,

    /// <summary>
    /// Transfer could not start because backend/session was not ready.
    /// <para>后端或会话未就绪，无法开始传输。</para>
    /// </summary>
    NotReady = 4,

    /// <summary>
    /// Transfer was canceled.
    /// <para>传输被取消。</para>
    /// </summary>
    Canceled = 5,

    /// <summary>
    /// Transfer stopped with a non-fatal native error (partial data may be available).
    /// <para>传输因非致命原生错误停止（可能已传输部分数据）。</para>
    /// </summary>
    Error = 6
}

/// <summary>
/// Represents a structured USB transfer event.
/// <para>表示结构化 USB 传输事件。</para>
/// </summary>
public sealed class UsbTransferEvent
{
    /// <summary>
    /// Gets or sets backend name.
    /// <para>获取或设置后端名称。</para>
    /// </summary>
    /// <value>The USB backend that produced this event, e.g. a native LibUsb or WinUSB label; empty when the emitter did not record one.
    /// <para>产生此事件的 USB 后端，例如原生 LibUsb 或 WinUSB 标识；发射方未记录时为空字符串。</para></value>
    public string Backend { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets device path.
    /// <para>获取或设置设备路径。</para>
    /// </summary>
    /// <value>The operating-system device path identifying the target device for this transfer; empty when the emitter did not know it.
    /// <para>标识本次传输目标设备的操作系统设备路径；发射方未知时为空字符串。</para></value>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets transfer operation kind.
    /// <para>获取或设置传输操作类型。</para>
    /// </summary>
    /// <value>The kind of operation recorded by this event, one of <see cref="UsbTransferOperation"/>.
    /// <para>此事件记录的操作类型，取值来自 <see cref="UsbTransferOperation"/>。</para></value>
    public UsbTransferOperation Operation { get; set; }

    /// <summary>
    /// Gets or sets requested bytes.
    /// <para>获取或设置请求字节数。</para>
    /// </summary>
    /// <value>The byte count the caller asked the backend to move for this operation.
    /// <para>本次传输调用方要求后端搬运的字节数。</para></value>
    public int RequestedBytes { get; set; }

    /// <summary>
    /// Gets or sets transferred bytes.
    /// <para>获取或设置实际传输字节数。</para>
    /// </summary>
    /// <value>The byte count the backend actually moved before the outcome; may be smaller than <see cref="RequestedBytes"/> on a short transfer or timeout.
    /// <para>后端在给出结果前实际搬运的字节数；短传输或超时时可能小于 <see cref="RequestedBytes"/>。</para></value>
    public int TransferredBytes { get; set; }

    /// <summary>
    /// Gets or sets timeout in milliseconds.
    /// <para>获取或设置超时（毫秒）。</para>
    /// </summary>
    /// <value>The timeout applied to this transfer; 0 means the backend was invoked without an explicit timeout.
    /// <para>本次传输应用的超时；0 表示调用后端时未指定显式超时。</para></value>
    public int TimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets retry count.
    /// <para>获取或设置重试次数。</para>
    /// </summary>
    /// <value>The number of retries already attempted when this event was emitted; 0 on the first try.
    /// <para>发出此事件时已经尝试的重试次数；首次尝试为 0。</para></value>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets native error code if available.
    /// <para>获取或设置原生错误码（若可用）。</para>
    /// </summary>
    /// <value>The raw backend error code (Win32 / libusb) associated with the outcome, or <see langword="null"/> when the backend reported none.
    /// <para>与结果相关的原始后端错误码（Win32 / libusb）；后端未上报时为 <see langword="null"/>。</para></value>
    public int? NativeErrorCode { get; set; }

    /// <summary>
    /// Gets or sets elapsed Time in milliseconds.
    /// <para>获取或设置耗时（毫秒）。</para>
    /// </summary>
    /// <value>Wall-clock time the backend spent on this transfer, measured by the emitter.
    /// <para>发射方测量的后端在此传输上消耗的挂钟时间。</para></value>
    public long ElapsedMs { get; set; }

    /// <summary>
    /// Gets or sets operation outcome.
    /// <para>获取或设置操作结果。</para>
    /// </summary>
    /// <value>The terminal status of the transfer, one of <see cref="UsbTransferOutcome"/>.
    /// <para>传输的最终状态，取值来自 <see cref="UsbTransferOutcome"/>。</para></value>
    public UsbTransferOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets optional Message.
    /// <para>获取或设置可选消息。</para>
    /// </summary>
    /// <value>A human-readable diagnostic attached by the emitter, or <see langword="null"/> when none was supplied.
    /// <para>发射方附带的人类可读诊断文本；未提供时为 <see langword="null"/>。</para></value>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the raw 8-byte USB setup packet (control transfers, opt-in frame capture).
    /// <para>获取或设置原始 8 字节 USB setup 包（控制传输，可选抓帧）。</para>
    /// </summary>
    /// <value>The captured control-transfer setup packet when <see cref="UsbTrace.CaptureFrames"/> is enabled, otherwise <see langword="null"/>.
    /// <para>启用 <see cref="UsbTrace.CaptureFrames"/> 时捕获到的控制传输 setup 包，否则为 <see langword="null"/>。</para></value>
    public byte[]? SetupPacket { get; set; }

    /// <summary>
    /// Gets or sets the raw payload bytes (bulk transfers, opt-in frame capture, capped).
    /// <para>获取或设置原始载荷字节（批量传输，可选抓帧，有截断上限）。</para>
    /// </summary>
    /// <value>The captured bulk-transfer payload when <see cref="UsbTrace.CaptureFrames"/> is enabled, truncated to <see cref="UsbTrace.MaxCaptureBytes"/>, otherwise <see langword="null"/>.
    /// <para>启用 <see cref="UsbTrace.CaptureFrames"/> 时捕获到的批量传输载荷，最多截断至 <see cref="UsbTrace.MaxCaptureBytes"/>，否则为 <see langword="null"/>。</para></value>
    public byte[]? Payload { get; set; }
}