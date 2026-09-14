namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo;

public sealed class UnsupportedOppVersionException : OppException
{
    /// <summary>
    /// Initializes a new instance of the UnsupportedOppVersionException class with a specified error message.
    /// <para>使用指定错误消息初始化 UnsupportedOppVersionException 类的新实例。</para>
    /// </summary>
    public UnsupportedOppVersionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the UnsupportedOppVersionException class with a specified error message and a reference to the inner exception.
    /// <para>使用指定错误消息和内部异常初始化 UnsupportedOppVersionException 类的新实例。</para>
    /// </summary>
    public UnsupportedOppVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}