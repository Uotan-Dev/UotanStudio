namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0;

/// <summary>
/// Base exception for NB0 firmware parsing, extraction and packing failures.
/// <para>NB0 固件解析、提取与打包失败时的基础异常。</para>
/// </summary>
public class Nb0Exception : Exception
{
    /// <summary>
    /// Initializes a new instance of Nb0Exception with a specified error message.
    /// <para>使用指定的错误消息初始化 Nb0Exception 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the failure. <para>描述失败原因的消息。</para>
    /// </param>
    public Nb0Exception(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of Nb0Exception with a specified error message and inner exception.
    /// <para>使用指定的错误消息和内部异常初始化 Nb0Exception 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the failure. <para>描述失败原因的消息。</para>
    /// </param>
    /// <param name="innerException">
    /// The exception that caused this failure. <para>导致此失败的内部异常。</para>
    /// </param>
    public Nb0Exception(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Raised when NB0 structure fails validation: an entry count beyond the supported bound, a header
/// table larger than the file, or an entry range that overflows or exceeds the stream length.
/// <para>当 NB0 结构校验失败时抛出：条目数超出支持上限、头部表大于文件，或条目范围溢出并超出流长度。</para>
/// </summary>
public sealed class Nb0CorruptedException : Nb0Exception
{
    /// <summary>
    /// Initializes a new instance of Nb0CorruptedException with a specified error message.
    /// <para>使用指定的错误消息初始化 Nb0CorruptedException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the structural violation. <para>描述结构违规情况的说明。</para>
    /// </param>
    public Nb0CorruptedException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of Nb0CorruptedException with a specified error message and inner exception.
    /// <para>使用指定的错误消息和内部异常初始化 Nb0CorruptedException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the structural violation. <para>描述结构违规情况的说明。</para>
    /// </param>
    /// <param name="innerException">
    /// The exception that caused this failure. <para>导致此失败的内部异常。</para>
    /// </param>
    public Nb0CorruptedException(string message, Exception innerException) : base(message, innerException) { }
}