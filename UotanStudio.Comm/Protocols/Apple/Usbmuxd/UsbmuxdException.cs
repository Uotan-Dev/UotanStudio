namespace UotanStudio.Comm.Protocols.Apple.Usbmuxd;

/// <summary>
/// Raised when the usbmuxd daemon rejects a request or the multiplexer protocol fails.
/// <para>usbmuxd 守护进程拒绝请求或复用协议失败时抛出。</para>
/// </summary>
public sealed class UsbmuxdException : IOException
{
    /// <summary>Gets the result code value used when the daemon did not report one. <para>获取守护进程未上报结果码时使用的值。</para></summary>
    public const uint UsbmuxdResultNone = uint.MaxValue;

    /// <summary>
    /// Creates the exception with a message and a daemon result code.
    /// <para>以消息与守护进程结果码创建异常。</para>
    /// </summary>
    /// <param name="message">The failure description. <para>失败描述。</para></param>
    /// <param name="resultCode">The usbmuxd result code, or <see cref="UsbmuxdResultNone"/> when absent. <para>usbmuxd 结果码；缺失时为 <see cref="UsbmuxdResultNone"/>。</para></param>
    public UsbmuxdException(string message, uint resultCode = UsbmuxdResultNone) : base(message) =>
        ResultCode = resultCode;

    /// <summary>
    /// Creates the exception wrapping an underlying I/O failure.
    /// <para>以底层 I/O 失败创建异常。</para>
    /// </summary>
    /// <param name="message">The failure description. <para>失败描述。</para></param>
    /// <param name="innerException">The underlying exception. <para>底层异常。</para></param>
    public UsbmuxdException(string message, Exception innerException) : base(message, innerException) =>
        ResultCode = UsbmuxdResultNone;

    /// <summary>Gets the usbmuxd result code, or <see cref="UsbmuxdResultNone"/>. <para>获取 usbmuxd 结果码；缺失时为 <see cref="UsbmuxdResultNone"/>。</para></summary>
    public uint ResultCode { get; }
}