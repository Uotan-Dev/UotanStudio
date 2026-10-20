namespace UotanStudio.Services.Devices;

/// <summary>
/// Retains newest logs within both entry and source-character budgets. Owner supplies synchronization.
/// <para>在条目数与原始字符数两项预算内保留最新日志，同步由持有方负责。</para>
/// </summary>
/// <param name="maximumEntries">Maximum number of retained entries. <para>保留条目的数量上限。</para></param>
/// <param name="maximumCharacters">Maximum number of retained source characters. <para>保留原始字符数的上限。</para></param>
public sealed class BoundedDeviceLogBuffer(int maximumEntries = 5000, int maximumCharacters = 2 * 1024 * 1024)
{
    private readonly int _maximumEntries = maximumEntries > 0
        ? maximumEntries
        : throw new ArgumentOutOfRangeException(nameof(maximumEntries));
    private readonly int _maximumCharacters = maximumCharacters > 0
        ? maximumCharacters
        : throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
    private readonly Queue<AdbLogcatEntry> _entries = new();
    /// <summary>
    /// Current source-character count, excluding display formatting.
    /// <para>当前原始字符数，不含显示格式化。</para>
    /// </summary>
    /// <value>The summed length of the retained raw lines. <para>已保留原始行的长度总和。</para></value>
    public int CharacterCount { get; private set; }
    /// <summary>
    /// Retained entries in arrival order.
    /// <para>按到达顺序保留的条目。</para>
    /// </summary>
    /// <value>The live sequence of retained entries. <para>当前保留条目的实时序列。</para></value>
    public IEnumerable<AdbLogcatEntry> Entries => _entries;
    /// <summary>
    /// Number of retained entries.
    /// <para>保留条目的数量。</para>
    /// </summary>
    /// <value>The current entry count. <para>当前的条目数量。</para></value>
    public int Count => _entries.Count;

    /// <summary>
    /// Adds an entry and reports each oldest entry evicted by either limit.
    /// <para>添加条目，并按任一上限淘汰时上报每个最旧的被驱逐条目。</para>
    /// </summary>
    /// <param name="entry">Entry to retain. <para>要保留的条目。</para></param>
    /// <param name="evicted">Callback per evicted entry; null to ignore eviction. <para>每个被驱逐条目的回调；为 null 时忽略驱逐通知。</para></param>
    public void Add(AdbLogcatEntry entry, Action<AdbLogcatEntry>? evicted = null)
    {
        if (entry.RawText.Length > _maximumCharacters)
        {
            evicted?.Invoke(entry);
            return;
        }

        _entries.Enqueue(entry);
        CharacterCount += entry.RawText.Length;
        while (_entries.Count > _maximumEntries || CharacterCount > _maximumCharacters)
        {
            var removed = Take();
            evicted?.Invoke(removed);
        }
    }

    /// <summary>
    /// Removes the oldest entry.
    /// <para>移除最旧的条目。</para>
    /// </summary>
    /// <returns>The entry that was at the head of the buffer. <para>原先位于缓冲区头部的条目。</para></returns>
    public AdbLogcatEntry Take()
    {
        var entry = _entries.Dequeue();
        CharacterCount -= entry.RawText.Length;
        return entry;
    }

    /// <summary>
    /// Releases all retained entries.
    /// <para>释放全部已保留的条目。</para>
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        CharacterCount = 0;
    }
}