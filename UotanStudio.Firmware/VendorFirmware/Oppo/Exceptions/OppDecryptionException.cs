namespace UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;

/// <summary>
/// Raised when an OPPO encrypted firmware payload cannot be decrypted, for example when no known key
/// produces valid plaintext.
/// <para>当 OPPO 加密固件负载无法解密时抛出，例如没有任何已知密钥能产生有效明文。</para>
/// </summary>
public sealed class OppDecryptionException : OppException
{
    /// <summary>
    /// Initializes a new instance of the OppDecryptionException class with a specified error message.
    /// <para>使用指定错误消息初始化 OppDecryptionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the decryption failure. <para>描述该解密失败的消息。</para></param>
    public OppDecryptionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OppDecryptionException class with a specified error message and a reference to the inner exception.
    /// <para>使用指定错误消息和内部异常初始化 OppDecryptionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the decryption failure. <para>描述该解密失败的消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致本次失败的内部异常。</para></param>
    public OppDecryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}