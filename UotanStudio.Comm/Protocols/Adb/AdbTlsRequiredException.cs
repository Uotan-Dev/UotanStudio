namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Raised when a plaintext ADB transport receives the peer's A_STLS offer: the target requires the
/// secure transport, so the caller must reopen the endpoint over TLS instead of continuing in plaintext.
/// <para>当明文 ADB 传输收到对端的 A_STLS 提议时抛出：目标要求安全传输，调用方必须改用 TLS
/// 重新打开端点，而不能继续使用明文。</para>
/// </summary>
public sealed class AdbTlsRequiredException : IOException
{
    /// <summary>
    /// Initializes a new instance with the default message.
    /// <para>使用默认消息初始化新实例。</para>
    /// </summary>
    public AdbTlsRequiredException()
        : base("The ADB peer requires a TLS connection.")
    {
    }

    /// <summary>
    /// Initializes a new instance with a caller-supplied message.
    /// <para>使用调用方提供的消息初始化新实例。</para>
    /// </summary>
    /// <param name="message">The diagnostic message. <para>诊断消息。</para></param>
    public AdbTlsRequiredException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with a caller-supplied message and inner failure.
    /// <para>使用调用方提供的消息与内部故障初始化新实例。</para>
    /// </summary>
    /// <param name="message">The diagnostic message. <para>诊断消息。</para></param>
    /// <param name="innerException">The failure that caused this exception. <para>导致本异常的内部故障。</para></param>
    public AdbTlsRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}