using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb;

/// <summary>
/// Centralizes shared USB transfer policy constants and timeout normalization logic.
/// <para>集中管理共享的 USB 传输策略常量及超时规范化逻辑。</para>
/// The class is public so external protocol layers (e.g. UotanStudio.Comm.EDL) can tune
/// the process-wide retry policy and timeout normalization without forking the library.
/// <para>此类公开，使外部协议层（如 UotanStudio.Comm.EDL）无需分叉本库即可调整进程级
/// 重试策略与超时规范化。</para>
/// </summary>
internal static class UsbTransferPolicies
{
    /// <summary>
    /// The sentinel timeout value meaning "wait indefinitely".
    /// <para>表示"无限等待"的哨兵超时值。</para>
    /// Passing <see cref="InfiniteTimeoutMs"/> (-1) as a transfer timeout blocks until the
    /// transfer completes or the device disconnects; it is the only way to express
    /// "no deadline" (0 and other non-positive values fall back to the default timeout).
    /// <para>将 <see cref="InfiniteTimeoutMs"/>（-1）作为传输超时传入时会阻塞直到传输完成
    /// 或设备断开；这是表达"无期限"的唯一方式（0 及其他非正值回退到默认超时）。</para>
    /// </summary>
    public const int InfiniteTimeoutMs = -1;

    /// <summary>
    /// The default timeout in milliseconds for USB transfers.
    /// <para>USB 传输的默认超时时间（毫秒）。</para>
    /// </summary>
    public const int DefaultTimeoutMs = 5000;

    /// <summary>
    /// The default timeout in milliseconds for WinUSB operations.
    /// <para>WinUSB 操作的默认超时时间（毫秒）。</para>
    /// </summary>
    public const int WinUsbDefaultTimeoutMs = 60000;

    /// <summary>
    /// The maximum chunk size in bytes for a single USB transfer.
    /// <para>单次 USB 传输的最大块大小（字节）。</para>
    /// </summary>
    public const int MaxChunkSize = 1024 * 1024;

    /// <summary>
    /// The maximum bulk transfer size for the Linux usbfs backend.
    /// <para>Linux usbfs 后端的最大批量传输大小。</para>
    /// </summary>
    public const int LinuxUsbFsMaxBulkSize = 16384;

    /// <summary>
    /// The safety cap for a single read allocation requested by the caller (or derived from
    /// an untrusted protocol length field), preventing an OOM-sized <c>new byte[length]</c>.
    /// <para>单次读取分配的安全上限（由调用方指定或从不可信协议长度字段得出），
    /// 防止 OOM 级别的 <c>new byte[length]</c> 分配。</para>
    /// </summary>
    public const int MaxReadLength = 64 * 1024 * 1024;

    /// <summary>
    /// The maximum number of retries for recoverable Linux USB transfer errors.
    /// <para>可恢复的 Linux USB 传输错误的最大重试次数。</para>
    /// </summary>
    public const int LinuxMaxRetries = 5;

    /// <summary>
    /// Gets or sets the process-wide default retry policy for recoverable transfer errors.
    /// <para>获取或设置进程级的可恢复传输错误默认重试策略。</para>
    /// Setting <c>null</c> restores the built-in default (<see cref="LinuxMaxRetries"/>
    /// retries, 500 ms apart), so external callers can disable retries with
    /// <c>new UsbTransferRetryPolicy(0, 0)</c> and later re-enable the default by
    /// assigning <c>null</c>.
    /// <para>设为 <c>null</c> 时恢复内置默认值（<see cref="LinuxMaxRetries"/> 次重试、
    /// 间隔 500 ms）；外部调用方可用 <c>new UsbTransferRetryPolicy(0, 0)</c> 关闭重试，
    /// 之后再赋 <c>null</c> 恢复默认。</para>
    /// </summary>
    public static UsbTransferRetryPolicy DefaultRetryPolicy
    {
        get;
        set => field = value ?? new UsbTransferRetryPolicy(LinuxMaxRetries, retryDelayMs: 500);
    } = new UsbTransferRetryPolicy(LinuxMaxRetries, retryDelayMs: 500);

    /// <summary>
    /// Returns the effective timeout, substituting the default when the provided value is zero or negative.
    /// <para>返回有效的超时值；当提供的值为零或负数时替换为默认值。</para>
    /// The <see cref="InfiniteTimeoutMs"/> (-1) sentinel is preserved verbatim so callers can
    /// request an unbounded wait; all other non-positive values fall back to the default.
    /// <para><see cref="InfiniteTimeoutMs"/>（-1）哨兵值原样保留，供调用方请求无期限等待；
    /// 其余非正值回退到默认值。</para>
    /// </summary>
    /// <param name="timeoutMs">The requested timeout in milliseconds. <para>请求的超时时间（毫秒）。</para></param>
    /// <param name="defaultTimeoutMs">The fallback default timeout. <para>回退的默认超时时间。</para></param>
    /// <returns>The normalized timeout value. <para>规范化后的超时值。</para></returns>
    public static int NormalizeTimeout(int timeoutMs, int defaultTimeoutMs) => timeoutMs == InfiniteTimeoutMs ? InfiniteTimeoutMs : timeoutMs > 0 ? timeoutMs : defaultTimeoutMs;
}