namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;

/// <summary>
/// Base type for payload parsing, extraction and generation failures thrown by this library.
/// <para>本库抛出的 payload 解析、提取与生成失败的基类型。</para>
/// </summary>
public class PayloadException : Exception
{
    /// <summary>
    /// Initializes a new instance of the PayloadException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PayloadException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    public PayloadException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PayloadException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PayloadException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public PayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the leading bytes of an input do not match the <c>CrAU</c> payload magic.
/// <para>当输入起始字节与 <c>CrAU</c> payload 魔数不匹配时抛出。</para>
/// </summary>
public class InvalidPayloadMagicException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the InvalidPayloadMagicException class with a specified error message.
    /// <para>使用指定的错误消息初始化 InvalidPayloadMagicException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the mismatch. <para>描述该不匹配的错误消息。</para></param>
    public InvalidPayloadMagicException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the InvalidPayloadMagicException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 InvalidPayloadMagicException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the mismatch. <para>描述该不匹配的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public InvalidPayloadMagicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a payload declares a format version other than the version 2 handled by this library.
/// <para>当 payload 声明的格式版本不是本库处理的版本 2 时抛出。</para>
/// </summary>
public class UnsupportedPayloadVersionException : PayloadException
{
    /// <summary>
    /// Payload format version read from the header that this library rejects.
    /// <para>从头部读取且被本库拒绝的 payload 格式版本。</para>
    /// </summary>
    /// <value>
    /// Unsigned version value; <c>0</c> when the exception was created by a message-only constructor.
    /// <para>无符号版本值；由仅带消息的构造函数创建时为 <c>0</c>。</para>
    /// </value>
    public ulong Version { get; }

    /// <summary>
    /// Initializes a new instance of the UnsupportedPayloadVersionException class with the unsupported version value.
    /// <para>使用不支持的版本值初始化 UnsupportedPayloadVersionException 类的新实例。</para>
    /// </summary>
    /// <param name="version">Version value read from the payload header. <para>从 payload 头部读取的版本值。</para></param>
    public UnsupportedPayloadVersionException(ulong version)
        : base($"Unsupported payload version ({version}). This library only supports version 2.")
    {
        Version = version;
    }

    /// <summary>
    /// Initializes a new instance of the UnsupportedPayloadVersionException class with a specified error message and the unsupported version value.
    /// <para>使用指定的错误消息和不支持的版本值，初始化 UnsupportedPayloadVersionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="version">Version value read from the payload header. <para>从 payload 头部读取的版本值。</para></param>
    public UnsupportedPayloadVersionException(string message, ulong version)
        : base(message)
    {
        Version = version;
    }

    /// <summary>
    /// Initializes a new instance of the UnsupportedPayloadVersionException class with a specified error message.
    /// <para>使用指定的错误消息初始化 UnsupportedPayloadVersionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    public UnsupportedPayloadVersionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the UnsupportedPayloadVersionException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 UnsupportedPayloadVersionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public UnsupportedPayloadVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the payload manifest region cannot be read to its declared length or decoded.
/// <para>当无法按声明长度读取或解码 payload manifest 区域时抛出。</para>
/// </summary>
public class InvalidManifestException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the InvalidManifestException class with a specified error message.
    /// <para>使用指定的错误消息初始化 InvalidManifestException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the manifest defect. <para>描述 manifest 缺陷的错误消息。</para></param>
    public InvalidManifestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the InvalidManifestException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 InvalidManifestException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the manifest defect. <para>描述 manifest 缺陷的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public InvalidManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a payload contains an install operation this library cannot apply.
/// <para>当 payload 包含本库无法应用的安装操作时抛出。</para>
/// </summary>
public class PayloadUnsupportedOperationException : PayloadException
{
    /// <summary>
    /// Install operation type reported as unsupported.
    /// <para>被报告为不支持的安装操作类型。</para>
    /// </summary>
    /// <value>
    /// Operation type name given at construction, or <see langword="null"/> when a constructor
    /// without an operation type was used.
    /// <para>构造时给出的操作类型名称；使用不带操作类型的构造函数时为 <see langword="null"/>。</para>
    /// </value>
    public string? OperationType { get; }

    /// <summary>
    /// Initializes a new instance of the PayloadUnsupportedOperationException class with a specified error message and the unsupported operation type.
    /// <para>使用指定的错误消息和不支持的操作类型，初始化 PayloadUnsupportedOperationException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="operationType">Name of the unsupported operation type. <para>不支持的操作类型名称。</para></param>
    public PayloadUnsupportedOperationException(string message, string operationType)
        : base(message)
    {
        OperationType = operationType;
    }

    /// <summary>
    /// Initializes a new instance of the PayloadUnsupportedOperationException class with a specified error message, the unsupported operation type, and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息、不支持的操作类型和对作为此异常原因的内部异常的引用，初始化 PayloadUnsupportedOperationException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="operationType">Name of the unsupported operation type. <para>不支持的操作类型名称。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public PayloadUnsupportedOperationException(string message, string operationType, Exception innerException)
        : base(message, innerException)
    {
        OperationType = operationType;
    }

    /// <summary>
    /// Initializes a new instance of the PayloadUnsupportedOperationException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PayloadUnsupportedOperationException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    public PayloadUnsupportedOperationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PayloadUnsupportedOperationException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PayloadUnsupportedOperationException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public PayloadUnsupportedOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a delta payload declares a source-payload minor version outside the supported set.
/// <para>当差分 payload 声明的源 payload 次版本号超出支持范围时抛出。</para>
/// </summary>
public class DeltaPayloadNotSupportedException : PayloadException
{
    /// <summary>
    /// Source payload minor version that triggered the rejection.
    /// <para>触发拒绝的源 payload 次版本号。</para>
    /// </summary>
    /// <value>
    /// Value given at construction; supported values are <c>0</c>, <c>2</c>, <c>3</c>, <c>4</c>, <c>5</c>,
    /// <c>6</c>, <c>7</c>, <c>8</c> and <c>9</c>, and <c>0</c> results from a message-only constructor.
    /// <para>构造时给出的值；支持的取值为 <c>0</c>、<c>2</c>、<c>3</c>、<c>4</c>、<c>5</c>、<c>6</c>、<c>7</c>、<c>8</c>
    /// 和 <c>9</c>，由仅带消息的构造函数创建时为 <c>0</c>。</para>
    /// </value>
    public uint MinorVersion { get; }

    /// <summary>
    /// Initializes a new instance of the DeltaPayloadNotSupportedException class with the unsupported minor version.
    /// <para>使用不支持的次版本号初始化 DeltaPayloadNotSupportedException 类的新实例。</para>
    /// </summary>
    /// <param name="minorVersion">Minor version read from the source payload header. <para>从源 payload 头部读取的次版本号。</para></param>
    public DeltaPayloadNotSupportedException(uint minorVersion)
        : base($"Delta payload with minor_version={minorVersion} is not supported. Supported values are: 0, 2, 3, 4, 5, 6, 7, 8, 9.")
    {
        MinorVersion = minorVersion;
    }

    /// <summary>
    /// Initializes a new instance of the DeltaPayloadNotSupportedException class with a specified error message.
    /// <para>使用指定的错误消息初始化 DeltaPayloadNotSupportedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    public DeltaPayloadNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the DeltaPayloadNotSupportedException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 DeltaPayloadNotSupportedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public DeltaPayloadNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when extracting a partition image out of a payload fails.
/// <para>当从 payload 中提取分区镜像失败时抛出。</para>
/// </summary>
public class PartitionExtractionException : PayloadException
{
    /// <summary>
    /// Partition whose extraction failed.
    /// <para>提取失败的分区。</para>
    /// </summary>
    /// <value>
    /// Partition name given at construction, or <see langword="null"/> when a constructor without a
    /// partition name was used.
    /// <para>构造时给出的分区名称；使用不带分区名称的构造函数时为 <see langword="null"/>。</para>
    /// </value>
    public string? PartitionName { get; }

    /// <summary>
    /// Initializes a new instance of the PartitionExtractionException class with a specified error message and the partition name.
    /// <para>使用指定的错误消息和分区名称，初始化 PartitionExtractionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="partitionName">Name of the partition being extracted. <para>正在提取的分区名称。</para></param>
    public PartitionExtractionException(string message, string partitionName)
        : base(message)
    {
        PartitionName = partitionName;
    }

    /// <summary>
    /// Initializes a new instance of the PartitionExtractionException class with a specified error message, the partition name, and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息、分区名称和对作为此异常原因的内部异常的引用，初始化 PartitionExtractionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="partitionName">Name of the partition being extracted. <para>正在提取的分区名称。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public PartitionExtractionException(string message, string partitionName, Exception innerException)
        : base(message, innerException)
    {
        PartitionName = partitionName;
    }

    /// <summary>
    /// Initializes a new instance of the PartitionExtractionException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PartitionExtractionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    public PartitionExtractionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PartitionExtractionException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PartitionExtractionException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public PartitionExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when building or writing a payload violates a supported limit or fails.
/// <para>当构建或写入 payload 超出支持的限制或失败时抛出。</para>
/// </summary>
public class PayloadGenerationException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the PayloadGenerationException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PayloadGenerationException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the generation failure. <para>描述生成失败的错误消息。</para></param>
    public PayloadGenerationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PayloadGenerationException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PayloadGenerationException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the generation failure. <para>描述生成失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public PayloadGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}