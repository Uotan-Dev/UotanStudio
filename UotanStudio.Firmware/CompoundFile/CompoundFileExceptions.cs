namespace UotanStudio.Firmware.CompoundFile;

/// <summary>
/// Base type for compound file (OLE2/CFB) parsing and reading failures thrown by this library.
/// <para>本库抛出的复合文档（OLE2/CFB）解析与读取失败的基类型。</para>
/// </summary>
public class CompoundFileException : Exception
{
    /// <summary>
    /// Initializes a new instance of the CompoundFileException class with a specified error message.
    /// <para>使用指定的错误消息初始化 CompoundFileException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    public CompoundFileException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the CompoundFileException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 CompoundFileException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public CompoundFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the leading bytes of an input do not match the OLE2/CFB magic
/// (<c>D0 CF 11 E0 A1 B1 1A E1</c>).
/// <para>当输入起始字节与 OLE2/CFB 魔数（<c>D0 CF 11 E0 A1 B1 1A E1</c>）不匹配时抛出。</para>
/// </summary>
public class InvalidCompoundFileMagicException : CompoundFileException
{
    /// <summary>
    /// Initializes a new instance of the InvalidCompoundFileMagicException class with a specified error message.
    /// <para>使用指定的错误消息初始化 InvalidCompoundFileMagicException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the mismatch. <para>描述该不匹配的错误消息。</para></param>
    public InvalidCompoundFileMagicException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when a compound file declares a major version or sector shift this library does not support.
/// <para>当复合文档声明了本库不支持的主版本号或扇区移位时抛出。</para>
/// </summary>
public class UnsupportedCompoundFileVersionException : CompoundFileException
{
    /// <summary>
    /// Major version read from the header that this library rejects.
    /// <para>从头部读取且被本库拒绝的主版本号。</para>
    /// </summary>
    /// <value>
    /// Major version value from the header.
    /// <para>头部中的主版本号。</para>
    /// </value>
    public ushort MajorVersion { get; }

    /// <summary>
    /// Initializes a new instance of the UnsupportedCompoundFileVersionException class with the unsupported major version.
    /// <para>使用不支持的主版本号初始化 UnsupportedCompoundFileVersionException 类的新实例。</para>
    /// </summary>
    /// <param name="majorVersion">Major version read from the header. <para>从头部读取的主版本号。</para></param>
    public UnsupportedCompoundFileVersionException(ushort majorVersion)
        : base($"Unsupported compound file major version {majorVersion}. This library only supports version 3 (512-byte sectors) and version 4 (4096-byte sectors).")
    {
        MajorVersion = majorVersion;
    }
}

/// <summary>
/// Thrown when a compound file structure is truncated, self-inconsistent or violates a bounds limit;
/// the input is treated as untrusted, so every offset, sector index and chain length is validated.
/// <para>当复合文档结构被截断、自相矛盾或违反上限时抛出；输入视为不可信，所有偏移、扇区号与链长均被校验。</para>
/// </summary>
public class CompoundFileCorruptedException : CompoundFileException
{
    /// <summary>
    /// Initializes a new instance of the CompoundFileCorruptedException class with a specified error message.
    /// <para>使用指定的错误消息初始化 CompoundFileCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the structural defect. <para>描述该结构缺陷的错误消息。</para></param>
    public CompoundFileCorruptedException(string message)
        : base(message)
    {
    }
}