using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace UotanStudio.Services.Logging;

/// <summary>Publishes head eviction and appended log entries as range notifications.</summary>
public sealed class LogEntryCollection<T> : ObservableCollection<T>
{
    /// <summary>Removes a prefix and appends a batch with at most two collection events.</summary>
    public void Apply(int removeCount, IReadOnlyList<T> appended)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(removeCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(removeCount, Count);
        CheckReentrancy();
        if (removeCount > 0)
        {
            var removed = Items.Take(removeCount).ToArray();
            ((List<T>)Items).RemoveRange(0, removeCount);
            Changed();
            OnCollectionChanged(new(NotifyCollectionChangedAction.Remove, removed, 0));
        }
        if (appended.Count > 0)
        {
            var index = Count;
            var added = appended.ToArray();
            ((List<T>)Items).AddRange(added);
            Changed();
            OnCollectionChanged(new(NotifyCollectionChangedAction.Add, added, index));
        }
    }

    /// <summary>Replaces filtered results with one notification.</summary>
    public void Replace(IEnumerable<T> entries)
    {
        var replacement = entries.ToArray();
        CheckReentrancy();
        Items.Clear();
        ((List<T>)Items).AddRange(replacement);
        Changed();
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }

    private void Changed()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }
}
