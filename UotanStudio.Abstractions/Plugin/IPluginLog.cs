namespace UotanStudio.Abstractions.Plugin;

/// <summary>
/// <para>插件日志写入接口，统一收集插件运行日志。</para>
/// Plugin log writer interface, uniformly collecting plugin runtime logs.
/// </summary>
public interface IPluginLog
{
    /// <summary>
    /// <para>写入跟踪级别日志。</para>
    /// Writes a trace-level log entry.
    /// </summary>
    void Trace(string message);

    /// <summary>
    /// <para>写入调试级别日志。</para>
    /// Writes a debug-level log entry.
    /// </summary>
    void Debug(string message);

    /// <summary>
    /// <para>写入信息级别日志。</para>
    /// Writes an info-level log entry.
    /// </summary>
    void Info(string message);

    /// <summary>
    /// <para>写入警告级别日志。</para>
    /// Writes a warning-level log entry.
    /// </summary>
    void Warn(string message);

    /// <summary>
    /// <para>写入错误级别日志。</para>
    /// Writes an error-level log entry.
    /// </summary>
    void Error(string message);
}
