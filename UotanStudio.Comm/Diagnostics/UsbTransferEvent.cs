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
    public string Backend { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets device path.
    /// <para>获取或设置设备路径。</para>
    /// </summary>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets transfer operation kind.
    /// <para>获取或设置传输操作类型。</para>
    /// </summary>
    public UsbTransferOperation Operation { get; set; }

    /// <summary>
    /// Gets or sets requested bytes.
    /// <para>获取或设置请求字节数。</para>
    /// </summary>
    public int RequestedBytes { get; set; }

    /// <summary>
    /// Gets or sets transferred bytes.
    /// <para>获取或设置实际传输字节数。</para>
    /// </summary>
    public int TransferredBytes { get; set; }

    /// <summary>
    /// Gets or sets timeout in milliseconds.
    /// <para>获取或设置超时（毫秒）。</para>
    /// </summary>
    public int TimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets retry count.
    /// <para>获取或设置重试次数。</para>
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets native error code if available.
    /// <para>获取或设置原生错误码（若可用）。</para>
    /// </summary>
    public int? NativeErrorCode { get; set; }

    /// <summary>
    /// Gets or sets elapsed Time in milliseconds.
    /// <para>获取或设置耗时（毫秒）。</para>
    /// </summary>
    public long ElapsedMs { get; set; }

    /// <summary>
    /// Gets or sets operation outcome.
    /// <para>获取或设置操作结果。</para>
    /// </summary>
    public UsbTransferOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets optional Message.
    /// <para>获取或设置可选消息。</para>
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the raw 8-byte USB setup packet (control transfers, opt-in frame capture).
    /// <para>获取或设置原始 8 字节 USB setup 包（控制传输，可选抓帧）。</para>
    /// </summary>
    public byte[]? SetupPacket { get; set; }

    /// <summary>
    /// Gets or sets the raw payload bytes (bulk transfers, opt-in frame capture, capped).
    /// <para>获取或设置原始载荷字节（批量传输，可选抓帧，有截断上限）。</para>
    /// </summary>
    public byte[]? Payload { get; set; }
}