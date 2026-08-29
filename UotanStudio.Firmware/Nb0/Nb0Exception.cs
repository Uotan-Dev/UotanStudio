namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>NB0 固件格式操作中发生的异常的基类。</para>
/// The base exception class for errors that occur during NB0 firmware format operations.
/// </summary>
public class Nb0Exception : Exception
{
    /// <summary>
    /// <para>使用指定的错误消息初始化 Nb0Exception 的新实例。</para>
    /// Initializes a new instance of Nb0Exception with a specified error message.
    /// </summary>
    public Nb0Exception(string message) : base(message) { }

    /// <summary>
    /// <para>使用指定的错误消息和内部异常初始化 Nb0Exception 的新实例。</para>
    /// Initializes a new instance of Nb0Exception with a specified error message and inner exception.
    /// </summary>
    public Nb0Exception(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// <para>当 NB0 固件文件已损坏或格式无效时抛出的异常。</para>
/// The exception that is thrown when an NB0 firmware file is corrupted or has an invalid format.
/// </summary>
/// <remarks>
/// <para>使用指定的错误消息初始化 Nb0CorruptedException 的新实例。</para>
/// Initializes a new instance of Nb0CorruptedException with a specified error message.
/// </remarks>
public sealed class Nb0CorruptedException : Nb0Exception
{
    public Nb0CorruptedException(string message) : base(message) { }
    public Nb0CorruptedException(string message, Exception innerException) : base(message, innerException) { }
}