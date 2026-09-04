namespace UotanStudio.Firmware.Zucchini;

/// <summary>
/// The base exception thrown when a Zucchini binary delta patch cannot be applied.
/// <para>Zucchini 二进制差分补丁无法应用时抛出的基础异常。</para>
/// </summary>
public class ZucchiniException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ZucchiniException"/> class with a specified message.
    /// <para>使用指定消息初始化 <see cref="ZucchiniException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. <para>错误消息。</para></param>
    public ZucchiniException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZucchiniException"/> class with a specified message
    /// and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 <see cref="ZucchiniException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. <para>错误消息。</para></param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para></param>
    public ZucchiniException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a Zucchini patch has an invalid structure: bad magic or version, truncated input,
/// out-of-bounds or overflowing offsets/lengths, malformed varints, or inconsistent stream lengths.
/// <para>当 Zucchini 补丁结构非法时抛出：魔数或版本错误、输入截断、越界或溢出的偏移/长度、
/// 畸形 varint 或数据流长度不一致。</para>
/// </summary>
public class ZucchiniFormatException : ZucchiniException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ZucchiniFormatException"/> class with a specified message.
    /// <para>使用指定消息初始化 <see cref="ZucchiniFormatException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. <para>错误消息。</para></param>
    public ZucchiniFormatException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZucchiniFormatException"/> class with a specified message
    /// and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 <see cref="ZucchiniFormatException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. <para>错误消息。</para></param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para></param>
    public ZucchiniFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the CRC-32 of the old or new image does not match the value declared in the patch
/// header. This is a strong signal that the patch is corrupt or does not match the base image.
/// <para>当旧镜像或新镜像的 CRC-32 与补丁头部声明的值不一致时抛出。这是补丁损坏或与基座
/// 镜像不匹配的强信号。</para>
/// </summary>
public class ZucchiniChecksumException : ZucchiniException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ZucchiniChecksumException"/> class with a specified message.
    /// <para>使用指定消息初始化 <see cref="ZucchiniChecksumException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. <para>错误消息。</para></param>
    public ZucchiniChecksumException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZucchiniChecksumException"/> class with a specified message
    /// and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 <see cref="ZucchiniChecksumException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. <para>错误消息。</para></param>
    /// <param name="innerException">The exception that is the cause of the current exception.
    /// <para>作为当前异常原因的内部异常。</para></param>
    public ZucchiniChecksumException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a Zucchini patch element declares an executable type other than <see cref="ZucchiniExecutableType.Raw"/>.
/// Only RAW elements (no disassembly, no reference processing) are supported by this applier.
/// <para>当 Zucchini 补丁元素声明的可执行类型不是 <see cref="ZucchiniExecutableType.Raw"/> 时抛出。
/// 本应用器仅支持 RAW 元素（不做反汇编、不做引用处理）。</para>
/// </summary>
public class ZucchiniUnsupportedExecutableTypeException : ZucchiniException
{
    /// <summary>
    /// Gets the unsupported executable type value of the element (0 is RAW, everything else is unsupported).
    /// <para>获取元素不受支持的可执行类型数值（0 为 RAW，其余均不受支持）。</para>
    /// </summary>
    public uint ExeType { get; }

    /// <summary>
    /// Gets the element's new-image offset at which the unsupported element writes.
    /// <para>获取不受支持元素在新镜像中写入处的偏移。</para>
    /// </summary>
    public uint Offset { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZucchiniUnsupportedExecutableTypeException"/> class
    /// with the executable type value and the element's new-image offset.
    /// <para>使用可执行类型数值与元素的新镜像偏移初始化
    /// <see cref="ZucchiniUnsupportedExecutableTypeException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="exeType">The unsupported executable type value. <para>不受支持的可执行类型数值。</para></param>
    /// <param name="offset">The element's new-image offset. <para>元素的新镜像偏移。</para></param>
    public ZucchiniUnsupportedExecutableTypeException(uint exeType, uint offset)
        : base(
            $"Zucchini patch element at new offset {offset} uses executable type {exeType}; only RAW (0) elements are supported. " +
            $"位于新镜像偏移 {offset} 的 Zucchini 补丁元素使用可执行类型 {exeType}；仅支持 RAW (0) 元素。")
    {
        ExeType = exeType;
        Offset = offset;
    }
}