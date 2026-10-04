namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Exception thrown when a Streaming DLOAD protocol operation fails: a malformed HDLC frame, an
/// unexpected DIAG response identifier, a NAK-mapped status, or a violated address, length, or
/// partition-table bound.
/// <para>Streaming DLOAD（流式下载）协议操作失败时抛出的异常：HDLC 帧格式错误、DIAG 响应号不符、
/// 映射为失败的 NAK 状态，或违反地址、长度、分区表边界。</para>
/// </summary>
internal sealed class StreamingException : EdlException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingException"/> class.
    /// <para>初始化 <see cref="StreamingException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The error code. <para>错误码。</para></param>
    /// <param name="message">The error message. <para>错误消息。</para></param>
    internal StreamingException(EdlErrorCode errorCode, string message)
        : base(errorCode, message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingException"/> class.
    /// <para>初始化 <see cref="StreamingException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The error code. <para>错误码。</para></param>
    /// <param name="message">The error message. <para>错误消息。</para></param>
    /// <param name="inner">The inner exception. <para>内部异常。</para></param>
    internal StreamingException(EdlErrorCode errorCode, string message, Exception inner)
        : base(errorCode, message, inner)
    {
    }
}