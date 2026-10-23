namespace UotanStudio.Workflow.Execution;

/// <summary>
/// Tracks artifact paths published during the current workflow run; cleared at the start of each run.
/// <para>登记当前工作流运行期间发布的产物路径；每次运行开始时清空。</para>
/// </summary>
public static class NodeArtifactRegistry
{
    private static readonly Lock Gate = new();
    private static readonly List<string> Recorded = [];

    /// <summary>Raised whenever the recorded paths change.<para>记录的路径发生变化时触发。</para></summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Gets the artifact paths recorded for the current run, oldest first.
    /// <para>取得当前运行已登记的产物路径，按登记先后排列。</para>
    /// </summary>
    /// <value>A snapshot of the recorded paths.<para>已登记路径的快照。</para></value>
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

    /// <summary>
    /// Records one published artifact path; duplicates are ignored.
    /// <para>登记一个已发布的产物路径，重复项会被忽略。</para>
    /// </summary>
    /// <param name="path">The artifact path to record.<para>需要登记的产物路径。</para></param>
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

    /// <summary>
    /// Clears the registry when a new workflow run starts.
    /// <para>新的工作流运行开始时清空登记内容。</para>
    /// </summary>
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