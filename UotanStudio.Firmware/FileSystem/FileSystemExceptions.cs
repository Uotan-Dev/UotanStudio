namespace UotanStudio.Firmware.FileSystem;

/// <summary>
/// Base type for failures caused by damaged or unsupported source image or filesystem data thrown by this library.
/// <para>本库抛出的、由源镜像或文件系统数据损坏或不受支持导致的失败的基类型。</para>
/// </summary>
public abstract class FileSystemException : Exception
{
    /// <summary>
    /// Initializes a new instance of the FileSystemException class with a specified error message.
    /// <para>使用指定的错误消息初始化 FileSystemException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    protected FileSystemException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the FileSystemException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 FileSystemException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    protected FileSystemException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a source image or filesystem structure is truncated, malformed, inconsistent or otherwise corrupt.
/// <para>当源镜像或文件系统结构被截断、畸形、不一致或以其他方式损坏时抛出。</para>
/// </summary>
public sealed class FileSystemCorruptedException : FileSystemException
{
    /// <summary>
    /// Initializes a new instance of the FileSystemCorruptedException class with a specified error message.
    /// <para>使用指定的错误消息初始化 FileSystemCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the corruption. <para>描述该损坏的错误消息。</para></param>
    public FileSystemCorruptedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the FileSystemCorruptedException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 FileSystemCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the corruption. <para>描述该损坏的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public FileSystemCorruptedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a source image uses a filesystem feature that this library does not support editing, rebuilding or representing.
/// <para>当源镜像使用了本库不支持编辑、重建或表示的文件系统特性时抛出。</para>
/// </summary>
public sealed class FileSystemUnsupportedFeatureException : FileSystemException
{
    /// <summary>
    /// Initializes a new instance of the FileSystemUnsupportedFeatureException class with a specified error message.
    /// <para>使用指定的错误消息初始化 FileSystemUnsupportedFeatureException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the unsupported feature. <para>描述该不受支持特性的错误消息。</para></param>
    public FileSystemUnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the FileSystemUnsupportedFeatureException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 FileSystemUnsupportedFeatureException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the unsupported feature. <para>描述该不受支持特性的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public FileSystemUnsupportedFeatureException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}