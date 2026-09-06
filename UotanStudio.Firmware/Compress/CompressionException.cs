namespace UotanStudio.Firmware.Compress;

public class CompressionException : Exception
{
    /// <summary>
    /// Creates the exception with an error message.
    /// <para>以错误消息创建异常。</para>
    /// </summary>
    public CompressionException(string message) : base(message) { }

    /// <summary>
    /// Creates the exception with an error message and an inner exception.
    /// <para>以错误消息与内部异常创建异常。</para>
    /// </summary>
    public CompressionException(string message, Exception innerException) : base(message, innerException) { }
}
