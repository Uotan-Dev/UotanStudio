namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo;

public sealed class InvalidOppFormatException : OppException
{
    /// <summary>
    /// Initializes a new instance of the InvalidOppFormatException class with a specified error message.
    /// <para>使用指定错误消息初始化 InvalidOppFormatException 类的新实例。</para>
    /// </summary>
    public InvalidOppFormatException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the InvalidOppFormatException class with a specified error message and a reference to the inner exception.
    /// <para>使用指定错误消息和内部异常初始化 InvalidOppFormatException 类的新实例。</para>
    /// </summary>
    public InvalidOppFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}