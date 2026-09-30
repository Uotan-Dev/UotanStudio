namespace UotanStudio.Firmware.Executable;

/// <summary>
/// Base type for failures caused by damaged or unsupported executable (ELF or Qualcomm image) data thrown by this library.
/// <para>本库抛出的、由可执行文件（ELF 或高通镜像）数据损坏或不受支持导致的失败的基类型。</para>
/// </summary>
public abstract class ExecutableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the ExecutableException class with a specified error message.
    /// <para>使用指定的错误消息初始化 ExecutableException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    protected ExecutableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ExecutableException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 ExecutableException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    protected ExecutableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an ELF or Qualcomm image is truncated, malformed, inconsistent or otherwise corrupt.
/// <para>当 ELF 或高通镜像被截断、畸形、不一致或以其他方式损坏时抛出。</para>
/// </summary>
public sealed class ExecutableCorruptedException : ExecutableException
{
    /// <summary>
    /// Initializes a new instance of the ExecutableCorruptedException class with a specified error message.
    /// <para>使用指定的错误消息初始化 ExecutableCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the corruption. <para>描述该损坏的错误消息。</para></param>
    public ExecutableCorruptedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ExecutableCorruptedException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 ExecutableCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the corruption. <para>描述该损坏的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public ExecutableCorruptedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an ELF or Qualcomm image uses a container feature or variant that this library does not support analyzing.
/// <para>当 ELF 或高通镜像使用了本库不支持分析容器特性或变体时抛出。</para>
/// </summary>
public sealed class ExecutableUnsupportedFeatureException : ExecutableException
{
    /// <summary>
    /// Initializes a new instance of the ExecutableUnsupportedFeatureException class with a specified error message.
    /// <para>使用指定的错误消息初始化 ExecutableUnsupportedFeatureException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the unsupported feature. <para>描述该不受支持特性的错误消息。</para></param>
    public ExecutableUnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ExecutableUnsupportedFeatureException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 ExecutableUnsupportedFeatureException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the unsupported feature. <para>描述该不受支持特性的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public ExecutableUnsupportedFeatureException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}