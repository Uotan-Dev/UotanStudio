namespace UotanStudio.Services.Execution;

/// <summary>
/// <para>登记当前工作流运行发布的产物路径，使交付页面可以直接选用；每次运行开始时清空。</para>
/// Tracks artifact paths published during the current workflow run so the delivery page can pick them up; cleared on each run.
/// </summary>
public static class NodeArtifactRegistry
{
    private static readonly Lock Gate = new();
    private static readonly List<string> Recorded = [];

    /// <summary>Raised whenever the recorded paths change.</summary>
    public static event EventHandler? Changed;

    /// <summary>Gets the artifact paths recorded for the current run, oldest first.</summary>
    public static IReadOnlyList<string> PublishedPaths
    {
        get
        {
            lock (Gate)
            {
                return [.. Recorded];
            }
        }
    }

    /// <summary>Records one published artifact path; duplicates are ignored.</summary>
    public static void Record(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (Gate)
        {
            if (Recorded.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            Recorded.Add(path);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Clears the registry; called when a new workflow run starts.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            if (Recorded.Count == 0)
            {
                return;
            }
            Recorded.Clear();
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }
}