namespace UotanStudio.Firmware.Oppo;

/// <summary>
/// <para>当 OPPO 固件文件格式无效或已损坏时抛出的异常。</para>
/// The exception that is thrown when an OPPO firmware file format is invalid or corrupted.
/// </summary>
public sealed class InvalidOppFormatException : OppException
{
    /// <summary>
    /// <para>使用指定错误消息初始化 <see cref="InvalidOppFormatException"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="InvalidOppFormatException"/> class with a specified error message.
    /// </summary>
    /// <param name="message"><para>描述格式无效的错误消息。</para>The message that describes the invalid format error.</param>
    public InvalidOppFormatException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// <para>使用指定错误消息和内部异常初始化 <see cref="InvalidOppFormatException"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="InvalidOppFormatException"/> class with a specified error message and a reference to the inner exception.
    /// </summary>
    /// <param name="message"><para>描述格式无效的错误消息。</para>The message that describes the invalid format error.</param>
    /// <param name="innerException"><para>导致当前异常的内部异常。</para>The exception that is the cause of the current exception.</param>
    public InvalidOppFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}