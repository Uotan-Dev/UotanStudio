namespace UotanStudio.Firmware.AndroidMeta.Dex;

/// <summary>
/// Error raised when DEX bytes violate the format, carrying a message and optionally the underlying failure without additional payload.
/// <para>DEX 字节违反格式时抛出的错误，仅携带消息与可选的内部异常，不附加其他数据。</para>
/// </summary>
internal sealed class DexException : Exception
{
    /// <summary>
    /// Creates a DEX exception.
    /// <para>创建 DEX 异常。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the violated DEX constraint.
    /// <para>描述所违反 DEX 约束的消息。</para>
    /// </param>
    public DexException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a DEX exception with an inner exception.
    /// <para>创建带内部异常的 DEX 异常。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the violated DEX constraint.
    /// <para>描述所违反 DEX 约束的消息。</para>
    /// </param>
    /// <param name="inner">
    /// Earlier exception that triggered this failure, kept for diagnostics.
    /// <para>引发本次失败的先前异常，保留用于诊断。</para>
    /// </param>
    public DexException(string message, Exception inner) : base(message, inner)
    {
    }
}