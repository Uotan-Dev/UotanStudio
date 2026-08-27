namespace UotanStudio.Firmware.Dex;

/// <summary>
/// <para>DEX 解析或写出错误。</para>
/// DEX parse or write error.
/// </summary>
public sealed class DexException : Exception
{
    /// <summary>
    /// <para>创建 DEX 异常。</para>
    /// Creates a DEX exception.
    /// </summary>
    public DexException(string message) : base(message)
    {
    }

    /// <summary>
    /// <para>创建带内部异常的 DEX 异常。</para>
    /// Creates a DEX exception with an inner exception.
    /// </summary>
    public DexException(string message, Exception inner) : base(message, inner)
    {
    }
}