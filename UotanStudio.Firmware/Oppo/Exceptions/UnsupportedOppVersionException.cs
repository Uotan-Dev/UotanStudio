namespace UotanStudio.Firmware.Oppo;

/// <summary>
/// <para>当 OPPO 固件文件使用不支持的版本时抛出的异常。</para>
/// The exception that is thrown when an OPPO firmware file uses an unsupported version.
/// </summary>
public sealed class UnsupportedOppVersionException : OppException
{
    /// <summary>
    /// <para>使用指定错误消息初始化 <see cref="UnsupportedOppVersionException"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="UnsupportedOppVersionException"/> class with a specified error message.
    /// </summary>
    /// <param name="message"><para>描述不支持版本的错误消息。</para>The message that describes the unsupported version error.</param>
    public UnsupportedOppVersionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// <para>使用指定错误消息和内部异常初始化 <see cref="UnsupportedOppVersionException"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="UnsupportedOppVersionException"/> class with a specified error message and a reference to the inner exception.
    /// </summary>
    /// <param name="message"><para>描述不支持版本的错误消息。</para>The message that describes the unsupported version error.</param>
    /// <param name="innerException"><para>导致当前异常的内部异常。</para>The exception that is the cause of the current exception.</param>
    public UnsupportedOppVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}