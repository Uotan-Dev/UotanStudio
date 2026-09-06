namespace UotanStudio.Firmware.Oppo;

public sealed class OppDecryptionException : OppException
{
    /// <summary>
    /// Initializes a new instance of the OppDecryptionException class with a specified error message.
    /// <para>使用指定错误消息初始化 OppDecryptionException 类的新实例。</para>
    /// </summary>
    public OppDecryptionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OppDecryptionException class with a specified error message and a reference to the inner exception.
    /// <para>使用指定错误消息和内部异常初始化 OppDecryptionException 类的新实例。</para>
    /// </summary>
    public OppDecryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
