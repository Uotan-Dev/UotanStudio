namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;

public sealed class DexException : Exception
{
    /// <summary>
    /// Creates a DEX exception.
    /// <para>创建 DEX 异常。</para>
    /// </summary>
    public DexException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a DEX exception with an inner exception.
    /// <para>创建带内部异常的 DEX 异常。</para>
    /// </summary>
    public DexException(string message, Exception inner) : base(message, inner)
    {
    }
}