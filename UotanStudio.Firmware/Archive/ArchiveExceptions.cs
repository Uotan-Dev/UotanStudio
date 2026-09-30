namespace UotanStudio.Firmware.Archive;

/// <summary>
/// Base type for failures caused by damaged or unsupported archive container data thrown by this library.
/// <para>本库抛出的、由归档容器数据损坏或不受支持导致的失败的基类型。</para>
/// </summary>
public abstract class ArchiveException : Exception
{
    /// <summary>
    /// Initializes a new instance of the ArchiveException class with a specified error message.
    /// <para>使用指定的错误消息初始化 ArchiveException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    protected ArchiveException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ArchiveException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 ArchiveException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    protected ArchiveException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an archive container or its stored metadata is truncated, malformed, inconsistent or otherwise corrupt.
/// <para>当归档容器或其存储的元数据被截断、畸形、不一致或以其他方式损坏时抛出。</para>
/// </summary>
public sealed class ArchiveCorruptedException : ArchiveException
{
    /// <summary>
    /// Initializes a new instance of the ArchiveCorruptedException class with a specified error message.
    /// <para>使用指定的错误消息初始化 ArchiveCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the corruption. <para>描述该损坏的错误消息。</para></param>
    public ArchiveCorruptedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ArchiveCorruptedException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 ArchiveCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the corruption. <para>描述该损坏的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public ArchiveCorruptedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an archive uses a container, dialect or compression feature that this library does not support reading, editing or reproducing.
/// <para>当归档使用了本库不支持读取、编辑或复现的容器、方言或压缩特性时抛出。</para>
/// </summary>
public sealed class ArchiveUnsupportedFeatureException : ArchiveException
{
    /// <summary>
    /// Initializes a new instance of the ArchiveUnsupportedFeatureException class with a specified error message.
    /// <para>使用指定的错误消息初始化 ArchiveUnsupportedFeatureException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the unsupported feature. <para>描述该不受支持特性的错误消息。</para></param>
    public ArchiveUnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ArchiveUnsupportedFeatureException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 ArchiveUnsupportedFeatureException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the unsupported feature. <para>描述该不受支持特性的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public ArchiveUnsupportedFeatureException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}