namespace UotanStudio.Firmware.Oppo;

/// <summary>
/// <para>OPPO 固件处理过程中发生的异常的基类。</para>
/// The base exception class for errors that occur during OPPO firmware processing.
/// </summary>
public class OppException : Exception
{
    /// <summary>
    /// <para>使用指定错误消息初始化 <see cref="OppException"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="OppException"/> class with a specified error message.
    /// </summary>
    /// <param name="message"><para>描述错误的消息。</para>The message that describes the error.</param>
    public OppException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// <para>使用指定错误消息和内部异常初始化 <see cref="OppException"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="OppException"/> class with a specified error message and a reference to the inner exception.
    /// </summary>
    /// <param name="message"><para>描述错误的消息。</para>The message that describes the error.</param>
    /// <param name="innerException"><para>导致当前异常的内部异常。</para>The exception that is the cause of the current exception.</param>
    public OppException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}