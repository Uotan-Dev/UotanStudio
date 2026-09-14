namespace UotanStudio.Services.Projects;

/// <summary>Severity of a project diagnostic, independent of any application logging framework.</summary>
public enum ProjectLogLevel { Info, Warn, Error }

/// <summary>Optional process-wide diagnostic channel. It never owns project or workspace state.</summary>
public static class ProjectDiagnostics
{
    /// <summary>Hosts may subscribe for their lifetime to forward diagnostics to their logging system.</summary>
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