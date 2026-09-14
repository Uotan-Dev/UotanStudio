namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Diagnostics;

public sealed class NullOppLogger : IOppLogger
{
    public static NullOppLogger Instance { get; } = new();

    private NullOppLogger()
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public void Trace(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public void Debug(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public void Information(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public void Warning(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public void Warning(string message, Exception exception)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public void Error(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public void Error(string message, Exception exception)
    {
    }
}