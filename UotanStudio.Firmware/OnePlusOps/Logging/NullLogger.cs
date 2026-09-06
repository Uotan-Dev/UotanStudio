namespace UotanStudio.Firmware.OnePlusOps.Logging;

public class NullLogger : IOnePlusLogger
{
    public static readonly NullLogger Instance = new();

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    public void Trace(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    public void Debug(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    public void Information(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    public void Warning(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    public void Error(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    public void Critical(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    public void Log(LogLevel level, string message) { }
}
