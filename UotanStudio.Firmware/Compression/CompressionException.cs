namespace UotanStudio.Firmware.Compression;

/// <summary>
/// Signals a failure raised by the compression or decompression pipelines.
/// <para>表示压缩或解压流程中发生的失败。</para>
/// </summary>
internal class CompressionException : Exception
{
    /// <summary>
    /// Creates the exception with an error message.
    /// <para>以错误消息创建异常。</para>
    /// </summary>
    /// <param name="message">Message describing the failure. <para>描述失败原因的消息。</para></param>
    public CompressionException(string message) : base(message) { }

    /// <summary>
    /// Creates the exception with an error message and an inner exception.
    /// <para>以错误消息与内部异常创建异常。</para>
    /// </summary>
    /// <param name="message">Message describing the failure. <para>描述失败原因的消息。</para></param>
    /// <param name="innerException">The underlying cause being wrapped. <para>被包装的根本原因异常。</para></param>
    public CompressionException(string message, Exception innerException) : base(message, innerException) { }
}