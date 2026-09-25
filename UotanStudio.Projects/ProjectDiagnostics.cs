namespace UotanStudio.Services.Projects;

/// <summary>
/// Severity of a project diagnostic, independent of any application logging framework.
/// <para>项目诊断的严重级别，独立于任何应用日志框架。</para>
/// </summary>
public enum ProjectLogLevel
{
    /// <summary>Informational trace. <para>信息级跟踪。</para></summary>
    Info,

    /// <summary>Warning worth surfacing to the user. <para>值得向用户提示的警告。</para></summary>
    Warn,

    /// <summary>Error requiring attention. <para>需要关注的错误。</para></summary>
    Error
}

/// <summary>
/// Optional process-wide diagnostic channel. It never owns project or workspace state.
/// <para>可选的进程级诊断通道；它不拥有任何项目或工作区状态。</para>
/// </summary>
public static class ProjectDiagnostics
{
    /// <summary>
    /// Hosts may subscribe for their lifetime to forward diagnostics to their logging system.
    /// <para>宿主可在自身生命周期内订阅，以便将诊断信息转发到其日志系统。</para>
    /// </summary>
    public static event Action<ProjectLogLevel, string, string>? Message;

    internal static void Write(ProjectLogLevel level, string source, string message)
    {
        System.Diagnostics.Trace.WriteLine($"{source}: {message}");
        if (Message is not { } subscribers) return;
        foreach (Action<ProjectLogLevel, string, string> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(level, source, message); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
    }
}