namespace UotanStudio.Firmware.Zucchini;

public class ZucchiniException : Exception
{
    /// <summary>
    /// Initializes a new instance of the ZucchiniException class with a specified message.
    /// <para>使用指定消息初始化 ZucchiniException 类的新实例。</para>
    /// </summary>
    public ZucchiniException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ZucchiniException class with a specified message and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 ZucchiniException 类的新实例。</para>
    /// </summary>
    public ZucchiniException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class ZucchiniFormatException : ZucchiniException
{
    /// <summary>
    /// Initializes a new instance of the ZucchiniFormatException class with a specified message.
    /// <para>使用指定消息初始化 ZucchiniFormatException 类的新实例。</para>
    /// </summary>
    public ZucchiniFormatException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ZucchiniFormatException class with a specified message and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 ZucchiniFormatException 类的新实例。</para>
    /// </summary>
    public ZucchiniFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class ZucchiniChecksumException : ZucchiniException
{
    /// <summary>
    /// Initializes a new instance of the ZucchiniChecksumException class with a specified message.
    /// <para>使用指定消息初始化 ZucchiniChecksumException 类的新实例。</para>
    /// </summary>
    public ZucchiniChecksumException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ZucchiniChecksumException class with a specified message and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 ZucchiniChecksumException 类的新实例。</para>
    /// </summary>
    public ZucchiniChecksumException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class ZucchiniUnsupportedExecutableTypeException : ZucchiniException
{
    public uint ExeType { get; }

    public uint Offset { get; }

    /// <summary>
    /// Initializes a new instance of the ZucchiniUnsupportedExecutableTypeException class with the executable type value and the element's new-image offset.
    /// <para>使用可执行类型数值与元素的新镜像偏移初始化 ZucchiniUnsupportedExecutableTypeException 类的新实例。</para>
    /// </summary>
    public ZucchiniUnsupportedExecutableTypeException(uint exeType, uint offset)
        : base(
            $"Zucchini patch element at new offset {offset} uses executable type {exeType}; only RAW (0) elements are supported. " +
            $"位于新镜像偏移 {offset} 的 Zucchini 补丁元素使用可执行类型 {exeType}；仅支持 RAW (0) 元素。")
    {
        ExeType = exeType;
        Offset = offset;
    }
}
