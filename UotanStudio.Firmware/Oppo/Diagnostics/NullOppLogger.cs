namespace UotanStudio.Firmware.Oppo.Diagnostics;

/// <summary>
/// <para><see cref="IOppLogger"/> 的空操作实现，不执行任何日志记录。用于不需要日志记录的场景。</para>
/// A no-op implementation of <see cref="IOppLogger"/> that performs no logging. Used in scenarios where logging is not needed.
/// </summary>
public sealed class NullOppLogger : IOppLogger
{
    /// <summary>
    /// <para>获取 <see cref="NullOppLogger"/> 的单例实例。</para>
    /// Gets the singleton instance of <see cref="NullOppLogger"/>.
    /// </summary>
    public static NullOppLogger Instance { get; } = new();

    private NullOppLogger()
    {
    }

    /// <inheritdoc />
    public void Trace(string message)
    {
    }

    /// <inheritdoc />
    public void Debug(string message)
    {
    }

    /// <inheritdoc />
    public void Information(string message)
    {
    }

    /// <inheritdoc />
    public void Warning(string message)
    {
    }

    /// <inheritdoc />
    public void Warning(string message, Exception exception)
    {
    }

    /// <inheritdoc />
    public void Error(string message)
    {
    }

    /// <inheritdoc />
    public void Error(string message, Exception exception)
    {
    }
}