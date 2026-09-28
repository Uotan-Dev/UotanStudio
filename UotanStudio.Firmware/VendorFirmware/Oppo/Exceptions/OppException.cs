namespace UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;

/// <summary>
/// Base class for every error raised while recognizing, parsing, decrypting or extracting an Oppo firmware package.
/// <para>在识别、解析、解密或提取 Oppo 固件包过程中抛出的所有错误的基类。</para>
/// </summary>
public class OppException : Exception
{
    /// <summary>
    /// Initializes a new instance of the OppException class with a specified error message.
    /// <para>使用指定错误消息初始化 OppException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述该错误的消息。</para>
    /// </param>
    public OppException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OppException class with a specified error message and a reference to the inner exception.
    /// <para>使用指定错误消息和内部异常初始化 OppException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述该错误的消息。</para>
    /// </param>
    /// <param name="innerException">
    /// The exception that caused this error.
    /// <para>导致本次错误的内部异常。</para>
    /// </param>
    public OppException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}