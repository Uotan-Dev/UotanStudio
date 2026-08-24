namespace UotanStudio.Firmware.Oppo;

/// <summary>
/// <para>当 OPPO 固件文件解密失败时抛出的异常。</para>
/// The exception that is thrown when decryption of an OPPO firmware file fails.
/// </summary>
public sealed class OppDecryptionException : OppException
{
    /// <summary>
    /// <para>使用指定错误消息初始化 <see cref="OppDecryptionException"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="OppDecryptionException"/> class with a specified error message.
    /// </summary>
    /// <param name="message"><para>描述解密失败的错误消息。</para>The message that describes the decryption failure.</param>
    public OppDecryptionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// <para>使用指定错误消息和内部异常初始化 <see cref="OppDecryptionException"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="OppDecryptionException"/> class with a specified error message and a reference to the inner exception.
    /// </summary>
    /// <param name="message"><para>描述解密失败的错误消息。</para>The message that describes the decryption failure.</param>
    /// <param name="innerException"><para>导致当前异常的内部异常。</para>The exception that is the cause of the current exception.</param>
    public OppDecryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}