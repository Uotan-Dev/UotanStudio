namespace UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;

/// <summary>
/// Raised when an OPPO container deviates from its expected structure, such as a wrong magic value,
/// a truncated file, or an unparsable size field.
/// <para>当 OPPO 容器偏离预期结构时抛出，例如魔数错误、文件被截断或大小字段无法解析。</para>
/// </summary>
internal sealed class InvalidOppFormatException : OppException
{
    /// <summary>
    /// Initializes a new instance of the InvalidOppFormatException class with a specified error message.
    /// <para>使用指定错误消息初始化 InvalidOppFormatException 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the format violation. <para>描述该格式违规的消息。</para></param>
    public InvalidOppFormatException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the InvalidOppFormatException class with a specified error message and a reference to the inner exception.
    /// <para>使用指定错误消息和内部异常初始化 InvalidOppFormatException 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the format violation. <para>描述该格式违规的消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致本次失败的内部异常。</para></param>
    public InvalidOppFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}