namespace UotanStudio.Services.Devices;

/// <summary>Retains newest logs within both entry and source-character budgets. Owner supplies synchronization.</summary>
public sealed class BoundedDeviceLogBuffer(int maximumEntries = 5000, int maximumCharacters = 2 * 1024 * 1024)
{
    private readonly Queue<AdbLogcatEntry> _entries = new();
    /// <summary>Current source-character count, excluding display formatting.</summary>
    public int CharacterCount { get; private set; }
    /// <summary>Retained entries in arrival order.</summary>
    public IEnumerable<AdbLogcatEntry> Entries => _entries;
    /// <summary>Number of retained entries.</summary>
    public int Count => _entries.Count;

    /// <summary>Adds an entry and reports each oldest entry evicted by either limit.</summary>
    public void Add(AdbLogcatEntry entry, Action<AdbLogcatEntry>? evicted = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        _entries.Enqueue(entry);
        CharacterCount += entry.RawText.Length;
        while (_entries.Count > maximumEntries || CharacterCount > maximumCharacters)
        {
            var removed = Take();
            evicted?.Invoke(removed);
        }
    }

    /// <summary>Removes the oldest entry.</summary>
    public AdbLogcatEntry Take()
    {
        var entry = _entries.Dequeue();
        CharacterCount -= entry.RawText.Length;
        return entry;
    }

    /// <summary>Releases all retained entries.</summary>
    public void Clear()
    {
        _entries.Clear();
        CharacterCount = 0;
    }
}
