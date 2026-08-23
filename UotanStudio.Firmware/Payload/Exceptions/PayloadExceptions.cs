namespace UotanStudio.Firmware.Payload.Exceptions;

/// <summary>
/// The base exception for all payload related errors.
/// <para>所有载荷相关错误的基础异常。</para>
/// </summary>
public class PayloadException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadException"/> class with a specified error message.
    /// <para>使用指定的错误消息初始化 <see cref="PayloadException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    public PayloadException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 <see cref="PayloadException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public PayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception that is thrown when the payload magic bytes do not match the expected "CrAU" signature.
/// <para>当载荷魔数字节与预期的 "CrAU" 签名不匹配时抛出的异常。</para>
/// </summary>
public class InvalidPayloadMagicException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidPayloadMagicException"/> class with a specified error message.
    /// <para>使用指定的错误消息初始化 <see cref="InvalidPayloadMagicException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the invalid magic bytes error.
    /// <para>描述无效魔数字节错误的消息。</para>
    /// </param>
    public InvalidPayloadMagicException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidPayloadMagicException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 <see cref="InvalidPayloadMagicException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public InvalidPayloadMagicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception that is thrown when the payload version is not supported by this library.
/// <para>当此库不支持该载荷版本时抛出的异常。</para>
/// </summary>
public class UnsupportedPayloadVersionException : PayloadException
{
    /// <summary>
    /// Gets the unsupported payload version that caused this exception.
    /// <para>获取导致此异常的不支持的载荷版本。</para>
    /// </summary>
    public ulong Version { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedPayloadVersionException"/> class with the unsupported version value.
    /// <para>使用不支持的版本值初始化 <see cref="UnsupportedPayloadVersionException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="version">The unsupported payload version number.
    /// <para>不支持的载荷版本号。</para>
    /// </param>
    public UnsupportedPayloadVersionException(ulong version)
        : base($"Unsupported payload version ({version}). This library only supports version 2.")
    {
        Version = version;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedPayloadVersionException"/> class with a specified error message
    /// and the unsupported version value.
    /// <para>使用指定的错误消息和不支持的版本值，初始化 <see cref="UnsupportedPayloadVersionException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the unsupported version error.
    /// <para>描述不支持的版本错误的消息。</para>
    /// </param>
    /// <param name="version">The unsupported payload version number.
    /// <para>不支持的载荷版本号。</para>
    /// </param>
    public UnsupportedPayloadVersionException(string message, ulong version)
        : base(message)
    {
        Version = version;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedPayloadVersionException"/> class with a specified error message.
    /// <para>使用指定的错误消息初始化 <see cref="UnsupportedPayloadVersionException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the unsupported version error.
    /// <para>描述不支持的版本错误的消息。</para>
    /// </param>
    public UnsupportedPayloadVersionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedPayloadVersionException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 <see cref="UnsupportedPayloadVersionException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public UnsupportedPayloadVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception that is thrown when the payload manifest cannot be parsed or is invalid.
/// <para>当载荷清单无法解析或无效时抛出的异常。</para>
/// </summary>
public class InvalidManifestException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidManifestException"/> class with a specified error message.
    /// <para>使用指定的错误消息初始化 <see cref="InvalidManifestException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the manifest parsing error.
    /// <para>描述清单解析错误的消息。</para>
    /// </param>
    public InvalidManifestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidManifestException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 <see cref="InvalidManifestException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public InvalidManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception that is thrown when an unsupported install operation type is encountered in the payload.
/// <para>当在载荷中遇到不支持的安装操作类型时抛出的异常。</para>
/// </summary>
public class PayloadUnsupportedOperationException : PayloadException
{
    /// <summary>
    /// Gets the type of the unsupported operation that caused this exception.
    /// <para>获取导致此异常的不支持的操作类型。</para>
    /// </summary>
    public string? OperationType { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadUnsupportedOperationException"/> class with a specified error message
    /// and the unsupported operation type.
    /// <para>使用指定的错误消息和不支持的操作类型，初始化 <see cref="PayloadUnsupportedOperationException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the unsupported operation error.
    /// <para>描述不支持的操作错误的消息。</para>
    /// </param>
    /// <param name="operationType">The type of the unsupported install operation.
    /// <para>不支持的安装操作类型。</para>
    /// </param>
    public PayloadUnsupportedOperationException(string message, string operationType)
        : base(message)
    {
        OperationType = operationType;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadUnsupportedOperationException"/> class with a specified error message,
    /// the unsupported operation type, and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息、不支持的操作类型和对作为此异常原因的内部异常的引用，初始化 <see cref="PayloadUnsupportedOperationException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="operationType">The type of the unsupported install operation.
    /// <para>不支持的安装操作类型。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public PayloadUnsupportedOperationException(string message, string operationType, Exception innerException)
        : base(message, innerException)
    {
        OperationType = operationType;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadUnsupportedOperationException"/> class with a specified error message.
    /// <para>使用指定的错误消息初始化 <see cref="PayloadUnsupportedOperationException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the unsupported operation error.
    /// <para>描述不支持的操作错误的消息。</para>
    /// </param>
    public PayloadUnsupportedOperationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadUnsupportedOperationException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 <see cref="PayloadUnsupportedOperationException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public PayloadUnsupportedOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception that is thrown when a delta payload is encountered but delta payloads are not supported.
/// <para>当遇到增量载荷但增量载荷不受支持时抛出的异常。</para>
/// </summary>
public class DeltaPayloadNotSupportedException : PayloadException
{
    /// <summary>
    /// Gets the unsupported minor version that caused this exception.
    /// <para>获取导致此异常的不支持的次版本号。</para>
    /// </summary>
    public uint MinorVersion { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeltaPayloadNotSupportedException"/> class with the unsupported minor version.
    /// <para>使用不支持的次版本号初始化 <see cref="DeltaPayloadNotSupportedException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="minorVersion">The unsupported minor version number.
    /// <para>不支持的次版本号。</para>
    /// </param>
    public DeltaPayloadNotSupportedException(uint minorVersion)
        : base($"Delta payload with minor_version={minorVersion} is not supported. Supported values are: 0, 2, 3, 4, 5, 6, 8, 9.")
    {
        MinorVersion = minorVersion;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeltaPayloadNotSupportedException"/> class with a specified error message.
    /// <para>使用指定的错误消息初始化 <see cref="DeltaPayloadNotSupportedException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the delta payload not supported error.
    /// <para>描述增量载荷不受支持错误的消息。</para>
    /// </param>
    public DeltaPayloadNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeltaPayloadNotSupportedException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 <see cref="DeltaPayloadNotSupportedException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public DeltaPayloadNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception that is thrown when a partition extraction operation fails.
/// <para>当分区提取操作失败时抛出的异常。</para>
/// </summary>
public class PartitionExtractionException : PayloadException
{
    /// <summary>
    /// Gets the name of the partition that failed to extract.
    /// <para>获取提取失败的分区名称。</para>
    /// </summary>
    public string? PartitionName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PartitionExtractionException"/> class with a specified error message
    /// and the partition name.
    /// <para>使用指定的错误消息和分区名称，初始化 <see cref="PartitionExtractionException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the partition extraction error.
    /// <para>描述分区提取错误的消息。</para>
    /// </param>
    /// <param name="partitionName">The name of the partition that failed to extract.
    /// <para>提取失败的分区名称。</para>
    /// </param>
    public PartitionExtractionException(string message, string partitionName)
        : base(message)
    {
        PartitionName = partitionName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PartitionExtractionException"/> class with a specified error message,
    /// the partition name, and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息、分区名称和对作为此异常原因的内部异常的引用，初始化 <see cref="PartitionExtractionException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="partitionName">The name of the partition that failed to extract.
    /// <para>提取失败的分区名称。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public PartitionExtractionException(string message, string partitionName, Exception innerException)
        : base(message, innerException)
    {
        PartitionName = partitionName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PartitionExtractionException"/> class with a specified error message.
    /// <para>使用指定的错误消息初始化 <see cref="PartitionExtractionException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the partition extraction error.
    /// <para>描述分区提取错误的消息。</para>
    /// </param>
    public PartitionExtractionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PartitionExtractionException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 <see cref="PartitionExtractionException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public PartitionExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception that is thrown when payload generation fails.
/// <para>当 payload 生成失败时抛出的异常。</para>
/// </summary>
public class PayloadGenerationException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadGenerationException"/> class with a specified error message.
    /// <para>使用指定的错误消息初始化 <see cref="PayloadGenerationException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    public PayloadGenerationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadGenerationException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 <see cref="PayloadGenerationException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.
    /// <para>解释异常原因的错误消息。</para>
    /// </param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para>
    /// </param>
    public PayloadGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}