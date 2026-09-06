namespace UotanStudio.Firmware.Payload.Exceptions;

public class PayloadException : Exception
{
    /// <summary>
    /// Initializes a new instance of the PayloadException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PayloadException 类的新实例。</para>
    /// </summary>
    public PayloadException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PayloadException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PayloadException 类的新实例。</para>
    /// </summary>
    public PayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class InvalidPayloadMagicException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the InvalidPayloadMagicException class with a specified error message.
    /// <para>使用指定的错误消息初始化 InvalidPayloadMagicException 类的新实例。</para>
    /// </summary>
    public InvalidPayloadMagicException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the InvalidPayloadMagicException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 InvalidPayloadMagicException 类的新实例。</para>
    /// </summary>
    public InvalidPayloadMagicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class UnsupportedPayloadVersionException : PayloadException
{
    public ulong Version { get; }

    /// <summary>
    /// Initializes a new instance of the UnsupportedPayloadVersionException class with the unsupported version value.
    /// <para>使用不支持的版本值初始化 UnsupportedPayloadVersionException 类的新实例。</para>
    /// </summary>
    public UnsupportedPayloadVersionException(ulong version)
        : base($"Unsupported payload version ({version}). This library only supports version 2.")
    {
        Version = version;
    }

    /// <summary>
    /// Initializes a new instance of the UnsupportedPayloadVersionException class with a specified error message and the unsupported version value.
    /// <para>使用指定的错误消息和不支持的版本值，初始化 UnsupportedPayloadVersionException 类的新实例。</para>
    /// </summary>
    public UnsupportedPayloadVersionException(string message, ulong version)
        : base(message)
    {
        Version = version;
    }

    /// <summary>
    /// Initializes a new instance of the UnsupportedPayloadVersionException class with a specified error message.
    /// <para>使用指定的错误消息初始化 UnsupportedPayloadVersionException 类的新实例。</para>
    /// </summary>
    public UnsupportedPayloadVersionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the UnsupportedPayloadVersionException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 UnsupportedPayloadVersionException 类的新实例。</para>
    /// </summary>
    public UnsupportedPayloadVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class InvalidManifestException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the InvalidManifestException class with a specified error message.
    /// <para>使用指定的错误消息初始化 InvalidManifestException 类的新实例。</para>
    /// </summary>
    public InvalidManifestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the InvalidManifestException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 InvalidManifestException 类的新实例。</para>
    /// </summary>
    public InvalidManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class PayloadUnsupportedOperationException : PayloadException
{
    public string? OperationType { get; }

    /// <summary>
    /// Initializes a new instance of the PayloadUnsupportedOperationException class with a specified error message and the unsupported operation type.
    /// <para>使用指定的错误消息和不支持的操作类型，初始化 PayloadUnsupportedOperationException 类的新实例。</para>
    /// </summary>
    public PayloadUnsupportedOperationException(string message, string operationType)
        : base(message)
    {
        OperationType = operationType;
    }

    /// <summary>
    /// Initializes a new instance of the PayloadUnsupportedOperationException class with a specified error message, the unsupported operation type, and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息、不支持的操作类型和对作为此异常原因的内部异常的引用，初始化 PayloadUnsupportedOperationException 类的新实例。</para>
    /// </summary>
    public PayloadUnsupportedOperationException(string message, string operationType, Exception innerException)
        : base(message, innerException)
    {
        OperationType = operationType;
    }

    /// <summary>
    /// Initializes a new instance of the PayloadUnsupportedOperationException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PayloadUnsupportedOperationException 类的新实例。</para>
    /// </summary>
    public PayloadUnsupportedOperationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PayloadUnsupportedOperationException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PayloadUnsupportedOperationException 类的新实例。</para>
    /// </summary>
    public PayloadUnsupportedOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class DeltaPayloadNotSupportedException : PayloadException
{
    public uint MinorVersion { get; }

    /// <summary>
    /// Initializes a new instance of the DeltaPayloadNotSupportedException class with the unsupported minor version.
    /// <para>使用不支持的次版本号初始化 DeltaPayloadNotSupportedException 类的新实例。</para>
    /// </summary>
    public DeltaPayloadNotSupportedException(uint minorVersion)
        : base($"Delta payload with minor_version={minorVersion} is not supported. Supported values are: 0, 2, 3, 4, 5, 6, 8, 9.")
    {
        MinorVersion = minorVersion;
    }

    /// <summary>
    /// Initializes a new instance of the DeltaPayloadNotSupportedException class with a specified error message.
    /// <para>使用指定的错误消息初始化 DeltaPayloadNotSupportedException 类的新实例。</para>
    /// </summary>
    public DeltaPayloadNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the DeltaPayloadNotSupportedException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 DeltaPayloadNotSupportedException 类的新实例。</para>
    /// </summary>
    public DeltaPayloadNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class PartitionExtractionException : PayloadException
{
    public string? PartitionName { get; }

    /// <summary>
    /// Initializes a new instance of the PartitionExtractionException class with a specified error message and the partition name.
    /// <para>使用指定的错误消息和分区名称，初始化 PartitionExtractionException 类的新实例。</para>
    /// </summary>
    public PartitionExtractionException(string message, string partitionName)
        : base(message)
    {
        PartitionName = partitionName;
    }

    /// <summary>
    /// Initializes a new instance of the PartitionExtractionException class with a specified error message, the partition name, and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息、分区名称和对作为此异常原因的内部异常的引用，初始化 PartitionExtractionException 类的新实例。</para>
    /// </summary>
    public PartitionExtractionException(string message, string partitionName, Exception innerException)
        : base(message, innerException)
    {
        PartitionName = partitionName;
    }

    /// <summary>
    /// Initializes a new instance of the PartitionExtractionException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PartitionExtractionException 类的新实例。</para>
    /// </summary>
    public PartitionExtractionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PartitionExtractionException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PartitionExtractionException 类的新实例。</para>
    /// </summary>
    public PartitionExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class PayloadGenerationException : PayloadException
{
    /// <summary>
    /// Initializes a new instance of the PayloadGenerationException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PayloadGenerationException 类的新实例。</para>
    /// </summary>
    public PayloadGenerationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PayloadGenerationException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PayloadGenerationException 类的新实例。</para>
    /// </summary>
    public PayloadGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
