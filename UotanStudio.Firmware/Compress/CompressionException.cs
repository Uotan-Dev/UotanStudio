namespace UotanStudio.Firmware.Compress;

/// <summary>
/// 压缩/解压过程中抛出的异常。
/// <para>Exception thrown during compression or decompression.</para>
/// </summary>
public class CompressionException : Exception
{
    /// <summary>
    /// 以错误消息创建异常。
    /// <para>Creates the exception with an error message.</para>
    /// </summary>
    /// <param name="message">错误描述。<para>The error message.</para></param>
    public CompressionException(string message) : base(message) { }

    /// <summary>
    /// 以错误消息与内部异常创建异常。
    /// <para>Creates the exception with an error message and an inner exception.</para>
    /// </summary>
    /// <param name="message">错误描述。<para>The error message.</para></param>
    /// <param name="innerException">内部异常。<para>The inner exception.</para></param>
    public CompressionException(string message, Exception innerException) : base(message, innerException) { }
}