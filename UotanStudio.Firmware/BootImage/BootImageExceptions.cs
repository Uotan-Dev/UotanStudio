namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// MagiskBoot 基础异常类型。
/// <para>The base exception type for MagiskBoot operations.</para>
/// </summary>
public class MagiskBootException : Exception
{
    /// <summary>
    /// 错误码。
    /// <para>The error code.</para>
    /// </summary>
    public int ErrorCode { get; }

    /// <summary>
    /// 初始化基础异常。
    /// <para>Initializes the base exception.</para>
    /// </summary>
    public MagiskBootException() : base()
    {
        ErrorCode = 0;
    }

    /// <summary>
    /// 使用指定错误码初始化基础异常。
    /// <para>Initializes the base exception with an error code.</para>
    /// </summary>
    /// <param name="errorCode">错误码。<para>The error code.</para></param>
    public MagiskBootException(int errorCode) : base()
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// 使用指定错误码与消息初始化基础异常。
    /// <para>Initializes the base exception with an error code and message.</para>
    /// </summary>
    public MagiskBootException(int errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// 使用指定消息初始化基础异常。
    /// <para>Initializes the base exception with a message.</para>
    /// </summary>
    public MagiskBootException(string message) : base(message)
    {
        ErrorCode = 0;
    }

    /// <summary>
    /// 使用指定消息与内部异常初始化基础异常。
    /// <para>Initializes the base exception with a message and inner exception.</para>
    /// </summary>
    public MagiskBootException(string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = 0;
    }

    /// <summary>
    /// 使用指定错误码、消息与内部异常初始化基础异常。
    /// <para>Initializes the base exception with an error code, message and inner exception.</para>
    /// </summary>
    public MagiskBootException(int errorCode, string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// 镜像解析失败时抛出的异常。
/// <para>Thrown when an image fails to parse.</para>
/// </summary>
public class ImageParseException : MagiskBootException
{
    /// <summary>
    /// 初始化镜像解析异常。
    /// <para>Initializes the image parse exception.</para>
    /// </summary>
    public ImageParseException() : base(1, "镜像解析失败") { }

    /// <summary>
    /// 使用指定消息初始化镜像解析异常。
    /// <para>Initializes the image parse exception with a message.</para>
    /// </summary>
    public ImageParseException(string message) : base(1, message) { }

    /// <summary>
    /// 使用指定消息与内部异常初始化镜像解析异常。
    /// <para>Initializes the image parse exception with a message and inner exception.</para>
    /// </summary>
    public ImageParseException(string message, Exception innerException) : base(1, message, innerException) { }
}

/// <summary>
/// 压缩或解压操作失败时抛出的异常。
/// <para>Thrown when a compression operation fails.</para>
/// </summary>
public class CompressionException : MagiskBootException
{
    /// <summary>
    /// 初始化压缩异常。
    /// <para>Initializes the compression exception.</para>
    /// </summary>
    public CompressionException() : base(1, "压缩操作失败") { }

    /// <summary>
    /// 使用指定消息初始化压缩异常。
    /// <para>Initializes the compression exception with a message.</para>
    /// </summary>
    public CompressionException(string message) : base(1, message) { }

    /// <summary>
    /// 使用指定消息与内部异常初始化压缩异常。
    /// <para>Initializes the compression exception with a message and inner exception.</para>
    /// </summary>
    public CompressionException(string message, Exception innerException) : base(1, message, innerException) { }
}

/// <summary>
/// ELF 补丁操作失败时抛出的异常。
/// <para>Thrown when an ELF patching operation fails.</para>
/// </summary>
public class ElfPatchingException : MagiskBootException
{
    /// <summary>
    /// 初始化 ELF 补丁异常。
    /// <para>Initializes the ELF patching exception.</para>
    /// </summary>
    public ElfPatchingException() : base(1, "ELF 补丁操作失败") { }

    /// <summary>
    /// 使用指定消息初始化 ELF 补丁异常。
    /// <para>Initializes the ELF patching exception with a message.</para>
    /// </summary>
    public ElfPatchingException(string message) : base(1, message) { }

    /// <summary>
    /// 使用指定消息与内部异常初始化 ELF 补丁异常。
    /// <para>Initializes the ELF patching exception with a message and inner exception.</para>
    /// </summary>
    public ElfPatchingException(string message, Exception innerException) : base(1, message, innerException) { }
}