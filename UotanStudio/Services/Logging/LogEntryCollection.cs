using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace UotanStudio.Services.Logging;

/// <summary>Publishes head eviction and appended log entries as range notifications.<para>以区间通知发布头部淘汰与追加的日志条目。</para></summary>
public sealed class LogEntryCollection<T> : ObservableCollection<T>
{
    /// <summary>Removes a prefix and appends a batch with at most two collection events.<para>移除前缀并追加一批，最多产生两次集合变更通知。</para></summary>
    /// <param name="removeCount">Number of leading entries to remove.<para>要移除的头部条目数。</para></param>
    /// <param name="appended">Entries appended after the removal.<para>移除之后追加的条目。</para></param>
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

    /// <summary>Replaces filtered results with one notification.<para>以一次通知替换筛选后的结果。</para></summary>
    /// <param name="entries">Entries forming the replacement.<para>构成替换内容的条目。</para></param>
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