namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Optional, per-session tuning knobs applied when opening a USB device session.
/// <para>打开 USB 设备会话时可选的、按会话生效的调优项。</para>
/// Every property is nullable; an all-null instance preserves the backend's built-in
/// behaviour (WinUSB 60 s default timeout, RAW_IO off, blocking no-data reads).
/// <para>所有属性均可空；全空的实例保持后端内置行为（WinUSB 60 秒默认超时、
/// RAW_IO 关闭、无数据读阻塞）。</para>
/// </summary>
internal sealed class UsbSessionOptions
{
    internal bool MetadataOnly { get; init; }

    /// <summary>
    /// Gets or sets the session-level default timeout in milliseconds, replacing the
    /// backend's hardcoded default (WinUSB currently 60000).
    /// <para>获取或设置会话级默认超时（毫秒），取代后端硬编码的默认值
    /// （WinUSB 当前为 60000）。</para>
    /// <c>null</c> keeps the backend default. The value flows into
    /// <see cref="IUsbDeviceSession.DefaultTimeoutMs"/> and the initial pipe timeout.
    /// <para><c>null</c> 时保持后端默认值。该值流入
    /// <see cref="IUsbDeviceSession.DefaultTimeoutMs"/> 及管道初始超时。</para>
    /// </summary>
    public int? DefaultTimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets the WinUSB bulk IN pipe timeout (PIPE_TRANSFER_TIMEOUT) in milliseconds.
    /// <para>获取或设置 WinUSB 批量 IN 管道超时（PIPE_TRANSFER_TIMEOUT，毫秒）。</para>
    /// <c>null</c> falls back to <see cref="DefaultTimeoutMs"/>, then the backend default.
    /// <para><c>null</c> 时回退到 <see cref="DefaultTimeoutMs"/>，再回退到后端默认值。</para>
    /// </summary>
    public int? ReadPipeTimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets the WinUSB bulk OUT pipe timeout (PIPE_TRANSFER_TIMEOUT) in milliseconds.
    /// <para>获取或设置 WinUSB 批量 OUT 管道超时（PIPE_TRANSFER_TIMEOUT，毫秒）。</para>
    /// <c>null</c> falls back to <see cref="DefaultTimeoutMs"/>, then the backend default.
    /// <para><c>null</c> 时回退到 <see cref="DefaultTimeoutMs"/>，再回退到后端默认值。</para>
    /// </summary>
    public int? WritePipeTimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the WinUSB bulk IN pipe uses RAW_IO.
    /// <para>获取或设置一个值，指示 WinUSB 批量 IN 管道是否使用 RAW_IO。</para>
    /// With RAW_IO enabled a read returns as soon as data is available, and returns 0
    /// bytes immediately when the device has no data (ERROR_NO_DATA) instead of blocking
    /// until the pipe timeout — the caller keeps its own polling timeout budget.
    /// <para>开启 RAW_IO 后，读操作有数据即返回；设备无数据时立即返回 0 字节
    /// （ERROR_NO_DATA）而非阻塞至管道超时——由调用方自行维持轮询超时预算。</para>
    /// <c>null</c>/<c>false</c> keeps the backend default (RAW_IO off, blocking reads).
    /// <para><c>null</c>/<c>false</c> 保持后端默认行为（RAW_IO 关闭、阻塞读）。</para>
    /// </summary>
    public bool? EnableRawIo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether partial bulk IN reads are allowed.
    /// <para>获取或设置一个值，指示是否允许批量 IN 部分读取。</para>
    /// When <c>true</c> the WinUSB bulk IN pipe is switched to RAW_IO so a read returns
    /// whatever is currently buffered instead of waiting to fill the full request;
    /// equivalent to <see cref="EnableRawIo"/> for the read direction.
    /// <para>为 <c>true</c> 时，WinUSB 批量 IN 管道切换为 RAW_IO，读操作返回当前已缓冲的
    /// 数据，而非等待填满整个请求；对读方向而言与 <see cref="EnableRawIo"/> 等价。</para>
    /// <c>null</c>/<c>false</c> keeps the backend default (blocking full reads).
    /// <para><c>null</c>/<c>false</c> 保持后端默认行为（阻塞至读满）。</para>
    /// </summary>
    public bool? AllowPartialReads { get; set; }
}