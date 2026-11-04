namespace UotanStudio.DD;

/// <summary>
/// Base type for failures raised while probing, validating or writing a disk image.
/// <para>探测、校验或写入磁盘镜像时抛出异常的基础类型。</para>
/// </summary>
public class FlashException : Exception
{
    /// <summary>
    /// Creates an exception with the given message.
    /// <para>使用给定消息创建异常。</para>
    /// </summary>
    /// <param name="message">Failure description.<para>失败描述。</para></param>
    public FlashException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception with the given message and underlying cause.
    /// <para>使用给定消息和底层原因创建异常。</para>
    /// </summary>
    /// <param name="message">Failure description.<para>失败描述。</para></param>
    /// <param name="innerException">Underlying cause.<para>底层原因。</para></param>
    public FlashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}