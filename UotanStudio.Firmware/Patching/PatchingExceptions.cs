namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Base type for failures caused by damaged or unsupported configuration text, binary patterns or image metadata thrown by this library.
/// <para>本库抛出的、由配置文本、二进制模式或镜像元数据损坏或不受支持导致的失败的基类型。</para>
/// </summary>
public abstract class PatchingException : Exception
{
    /// <summary>
    /// Initializes a new instance of the PatchingException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PatchingException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    protected PatchingException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PatchingException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PatchingException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    protected PatchingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when configuration text or image metadata is truncated, malformed, inconsistent or exceeds a limit derived from its own content.
/// <para>当配置文本或镜像元数据被截断、畸形、不一致，或超出由其自身内容推导的上限时抛出。</para>
/// </summary>
public sealed class PatchingCorruptedException : PatchingException
{
    /// <summary>
    /// Initializes a new instance of the PatchingCorruptedException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PatchingCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the corruption. <para>描述该损坏的错误消息。</para></param>
    public PatchingCorruptedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PatchingCorruptedException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PatchingCorruptedException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the corruption. <para>描述该损坏的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public PatchingCorruptedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a configuration dialect or editing operation uses a feature that this library does not support.
/// <para>当配置方言或编辑操作使用了本库不支持的特性时抛出。</para>
/// </summary>
public sealed class PatchingUnsupportedFeatureException : PatchingException
{
    /// <summary>
    /// Initializes a new instance of the PatchingUnsupportedFeatureException class with a specified error message.
    /// <para>使用指定的错误消息初始化 PatchingUnsupportedFeatureException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the unsupported feature. <para>描述该不受支持特性的错误消息。</para></param>
    public PatchingUnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the PatchingUnsupportedFeatureException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 PatchingUnsupportedFeatureException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the unsupported feature. <para>描述该不受支持特性的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public PatchingUnsupportedFeatureException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}