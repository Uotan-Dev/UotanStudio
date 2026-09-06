namespace UotanStudio.Firmware.Nb0;

public class Nb0Exception : Exception
{
    /// <summary>
    /// Initializes a new instance of Nb0Exception with a specified error message.
    /// <para>使用指定的错误消息初始化 Nb0Exception 的新实例。</para>
    /// </summary>
    public Nb0Exception(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of Nb0Exception with a specified error message and inner exception.
    /// <para>使用指定的错误消息和内部异常初始化 Nb0Exception 的新实例。</para>
    /// </summary>
    public Nb0Exception(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class Nb0CorruptedException : Nb0Exception
{
    public Nb0CorruptedException(string message) : base(message) { }
    public Nb0CorruptedException(string message, Exception innerException) : base(message, innerException) { }
}
