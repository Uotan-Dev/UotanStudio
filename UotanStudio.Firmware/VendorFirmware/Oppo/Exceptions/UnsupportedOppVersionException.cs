namespace UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;

/// <summary>
/// Raised when an Oppo package uses a detected format or version for which no parser is registered.
/// <para>当 Oppo 包检测到的格式或版本没有已注册的解析器时抛出。</para>
/// </summary>
public sealed class UnsupportedOppVersionException : OppException
{
    /// <summary>
    /// Initializes a new instance of the UnsupportedOppVersionException class with a specified error message.
    /// <para>使用指定错误消息初始化 UnsupportedOppVersionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述该错误的消息。</para>
    /// </param>
    public UnsupportedOppVersionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the UnsupportedOppVersionException class with a specified error message and a reference to the inner exception.
    /// <para>使用指定错误消息和内部异常初始化 UnsupportedOppVersionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述该错误的消息。</para>
    /// </param>
    /// <param name="innerException">
    /// The exception that caused this error.
    /// <para>导致本次错误的内部异常。</para>
    /// </param>
    public UnsupportedOppVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}