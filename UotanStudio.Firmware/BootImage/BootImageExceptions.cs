namespace UotanStudio.Firmware.BootImage;

public class MagiskBootException : Exception
{
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
    public MagiskBootException(int errorCode) : base()
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes the base exception with an error code and message.
    /// <para>使用指定错误码与消息初始化基础异常。</para>
    /// </summary>
    public MagiskBootException(int errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes the base exception with a message.
    /// <para>使用指定消息初始化基础异常。</para>
    /// </summary>
    public MagiskBootException(string message) : base(message)
    {
        ErrorCode = 0;
    }

    /// <summary>
    /// Initializes the base exception with a message and inner exception.
    /// <para>使用指定消息与内部异常初始化基础异常。</para>
    /// </summary>
    public MagiskBootException(string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = 0;
    }

    /// <summary>
    /// Initializes the base exception with an error code, message and inner exception.
    /// <para>使用指定错误码、消息与内部异常初始化基础异常。</para>
    /// </summary>
    public MagiskBootException(int errorCode, string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

public class ImageParseException : MagiskBootException
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
    public ImageParseException(string message) : base(1, message) { }

    /// <summary>
    /// Initializes the image parse exception with a message and inner exception.
    /// <para>使用指定消息与内部异常初始化镜像解析异常。</para>
    /// </summary>
    public ImageParseException(string message, Exception innerException) : base(1, message, innerException) { }
}

public class CompressionException : MagiskBootException
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
    public CompressionException(string message) : base(1, message) { }

    /// <summary>
    /// Initializes the compression exception with a message and inner exception.
    /// <para>使用指定消息与内部异常初始化压缩异常。</para>
    /// </summary>
    public CompressionException(string message, Exception innerException) : base(1, message, innerException) { }
}

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
    public ElfPatchingException(string message) : base(1, message) { }

    /// <summary>
    /// Initializes the ELF patching exception with a message and inner exception.
    /// <para>使用指定消息与内部异常初始化 ELF 补丁异常。</para>
    /// </summary>
    public ElfPatchingException(string message, Exception innerException) : base(1, message, innerException) { }
}
