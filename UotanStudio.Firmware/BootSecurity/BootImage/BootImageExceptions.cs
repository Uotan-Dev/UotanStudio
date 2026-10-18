namespace UotanStudio.Firmware.BootSecurity.BootImage;

/// <summary>
/// Base exception for boot image parsing, packing and patching failures.
/// <para>启动镜像解析、打包与补丁操作失败的基础异常。</para>
/// </summary>
public class MagiskBootException : Exception
{
    /// <summary>Numeric error code carried by the exception. <para>异常携带的数值错误码。</para></summary>
    /// <value>
    /// Read-only after construction; constructors without a code use <c>0</c>, the derived image-specific exception types pass <c>1</c>.
    /// <para>构造后只读；未提供错误码的构造函数使用 <c>0</c>，派生的镜像专用异常类型传入 <c>1</c>。</para>
    /// </value>
    public int ErrorCode { get; }

    /// <summary>
    /// Initializes the base exception.
    /// <para>初始化基础异常。</para>
    /// </summary>
    public MagiskBootException() : base()
    {
        ErrorCode = 0;
    }

    /// <summary>
    /// Initializes the base exception with an error code.
    /// <para>使用指定错误码初始化基础异常。</para>
    /// </summary>
    /// <param name="errorCode">
    /// Error code stored in <see cref="ErrorCode"/>. <para>存入 <see cref="ErrorCode"/> 的错误码。</para>
    /// </param>
    public MagiskBootException(int errorCode) : base()
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes the base exception with an error code and message.
    /// <para>使用指定错误码与消息初始化基础异常。</para>
    /// </summary>
    /// <param name="errorCode">
    /// Error code stored in <see cref="ErrorCode"/>. <para>存入 <see cref="ErrorCode"/> 的错误码。</para>
    /// </param>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>. <para>由 <see cref="Exception.Message"/> 返回的文本。</para>
    /// </param>
    public MagiskBootException(int errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes the base exception with a message.
    /// <para>使用指定消息初始化基础异常。</para>
    /// </summary>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>; the error code stays <c>0</c>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本；错误码保持为 <c>0</c>。</para>
    /// </param>
    public MagiskBootException(string message) : base(message)
    {
        ErrorCode = 0;
    }

    /// <summary>
    /// Initializes the base exception with a message and inner exception.
    /// <para>使用指定消息与内部异常初始化基础异常。</para>
    /// </summary>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>; the error code stays <c>0</c>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本；错误码保持为 <c>0</c>。</para>
    /// </param>
    /// <param name="innerException">
    /// Underlying cause exposed through <see cref="Exception.InnerException"/>.
    /// <para>通过 <see cref="Exception.InnerException"/> 暴露的根本原因。</para>
    /// </param>
    public MagiskBootException(string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = 0;
    }

    /// <summary>Initializes the base exception with an error code, message and inner exception.
    /// <para>使用指定错误码、消息与内部异常初始化基础异常。</para></summary>
    /// <param name="errorCode">Error code stored in <see cref="ErrorCode"/>.
    /// <para>存入 <see cref="ErrorCode"/> 的错误码。</para></param>
    /// <param name="message">Text returned by <see cref="Exception.Message"/>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本。</para></param>
    /// <param name="innerException">Underlying cause exposed through <see cref="Exception.InnerException"/>.
    /// <para>通过 <see cref="Exception.InnerException"/> 暴露的根本原因。</para></param>
    public MagiskBootException(int errorCode, string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Raised when a boot image cannot be parsed.
/// <para>启动镜像无法解析时抛出。</para>
/// </summary>
internal class ImageParseException : MagiskBootException
{
    /// <summary>
    /// Initializes the image parse exception.
    /// <para>初始化镜像解析异常。</para>
    /// </summary>
    public ImageParseException() : base(1, "镜像解析失败") { }

    /// <summary>
    /// Initializes the image parse exception with a message.
    /// <para>使用指定消息初始化镜像解析异常。</para>
    /// </summary>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>; the error code is fixed at <c>1</c>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本；错误码固定为 <c>1</c>。</para>
    /// </param>
    public ImageParseException(string message) : base(1, message) { }

    /// <summary>
    /// Initializes the image parse exception with a message and inner exception.
    /// <para>使用指定消息与内部异常初始化镜像解析异常。</para>
    /// </summary>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>; the error code is fixed at <c>1</c>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本；错误码固定为 <c>1</c>。</para>
    /// </param>
    /// <param name="innerException">
    /// Underlying cause exposed through <see cref="Exception.InnerException"/>.
    /// <para>通过 <see cref="Exception.InnerException"/> 暴露的根本原因。</para>
    /// </param>
    public ImageParseException(string message, Exception innerException) : base(1, message, innerException) { }
}

/// <summary>
/// Raised when a compression or decompression operation fails.
/// <para>压缩或解压操作失败时抛出。</para>
/// </summary>
internal class CompressionException : MagiskBootException
{
    /// <summary>
    /// Initializes the compression exception.
    /// <para>初始化压缩异常。</para>
    /// </summary>
    public CompressionException() : base(1, "压缩操作失败") { }

    /// <summary>
    /// Initializes the compression exception with a message.
    /// <para>使用指定消息初始化压缩异常。</para>
    /// </summary>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>; the error code is fixed at <c>1</c>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本；错误码固定为 <c>1</c>。</para>
    /// </param>
    public CompressionException(string message) : base(1, message) { }

    /// <summary>
    /// Initializes the compression exception with a message and inner exception.
    /// <para>使用指定消息与内部异常初始化压缩异常。</para>
    /// </summary>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>; the error code is fixed at <c>1</c>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本；错误码固定为 <c>1</c>。</para>
    /// </param>
    /// <param name="innerException">
    /// Underlying cause exposed through <see cref="Exception.InnerException"/>.
    /// <para>通过 <see cref="Exception.InnerException"/> 暴露的根本原因。</para>
    /// </param>
    public CompressionException(string message, Exception innerException) : base(1, message, innerException) { }
}

/// <summary>
/// Raised when an ELF patching operation fails.
/// <para>ELF 补丁操作失败时抛出。</para>
/// </summary>
public class ElfPatchingException : MagiskBootException
{
    /// <summary>
    /// Initializes the ELF patching exception.
    /// <para>初始化 ELF 补丁异常。</para>
    /// </summary>
    public ElfPatchingException() : base(1, "ELF 补丁操作失败") { }

    /// <summary>
    /// Initializes the ELF patching exception with a message.
    /// <para>使用指定消息初始化 ELF 补丁异常。</para>
    /// </summary>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>; the error code is fixed at <c>1</c>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本；错误码固定为 <c>1</c>。</para>
    /// </param>
    public ElfPatchingException(string message) : base(1, message) { }

    /// <summary>
    /// Initializes the ELF patching exception with a message and inner exception.
    /// <para>使用指定消息与内部异常初始化 ELF 补丁异常。</para>
    /// </summary>
    /// <param name="message">
    /// Text returned by <see cref="Exception.Message"/>; the error code is fixed at <c>1</c>.
    /// <para>由 <see cref="Exception.Message"/> 返回的文本；错误码固定为 <c>1</c>。</para>
    /// </param>
    /// <param name="innerException">
    /// Underlying cause exposed through <see cref="Exception.InnerException"/>.
    /// <para>通过 <see cref="Exception.InnerException"/> 暴露的根本原因。</para>
    /// </param>
    public ElfPatchingException(string message, Exception innerException) : base(1, message, innerException) { }
}