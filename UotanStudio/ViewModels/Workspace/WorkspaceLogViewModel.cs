using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Logging;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>工作区输出面板视图模型，仅订阅显示固件（Firmware）与烧录（Flash）通道的日志条目。
/// 专用于固件处理与设备烧录日志的实时展示。</para>
/// Workspace log panel view model, subscribing to the unified log service and filtering for
/// Firmware and Flash channel log entries for firmware processing and device flashing.
/// </summary>
internal partial class WorkspaceLogViewModel : Tool, IDisposable
{
    /// <summary>
    /// <para>日志条目视图模型。</para>
    /// Log entry view model.
    /// </summary>
    public sealed partial class LogEntryViewModel : ObservableObject
    {
        /// <summary><para>本地时间戳文本（HH:mm:ss）。Local timestamp text (HH:mm:ss).</para></summary>
        [ObservableProperty] private string _timestamp = string.Empty;

        /// <summary><para>日志级别。Log level.</para></summary>
        [ObservableProperty] private LogLevel _level = LogLevel.Info;

        /// <summary><para>日志来源。Log source.</para></summary>
        [ObservableProperty] private string _source = string.Empty;

        /// <summary><para>日志文本。Log text.</para></summary>
        [ObservableProperty] private string _message = string.Empty;

        /// <summary>Complete displayed and searchable text. 用于显示和搜索的完整日志行。</summary>
        public string Text { get; }

        public LogEntryViewModel(string timestamp, LogLevel level, string source, string message)
        {
            _timestamp = timestamp;
            _level = level;
            _source = source;
            _message = message;

            Text = $"{timestamp} [{level,-5}] {source} - {message}".ReplaceLineEndings("\n");
        }
    }

    /// <summary><para>当前展示的日志条目集合（已按级别与来源筛选）。Collection of displayed log entries (filtered).</para></summary>
    public LogEntryCollection<LogEntryViewModel> Entries { get; } = [];

    private readonly List<LogEntryViewModel> _allEntries = [];
    private const int MaxEntries = 5000;
    internal const int MaximumCharacters = 2 * 1024 * 1024;
    private const int BatchEntries = 250;
    private const int BatchCharacters = 128 * 1024;
    private long _historyCharacters;
    private Action? _unsubscribe;
    private bool _disposed;

    /// <summary><para>待刷新日志队列锁。</para>Gate for the pending log backlog.</para></summary>
    private readonly Lock _pendingGate = new();
    private readonly Queue<LogEntry> _pendingEntries = new();
    private long _pendingCharacters;
    private readonly Dictionary<string, int> _sourceCounts = new(StringComparer.Ordinal);

    /// <summary><para>100ms 批量刷新定时器，与全局日志面板共用同一批量约定。</para>100 ms flush timer, sharing the batched convention with the global log panel.</para></summary>
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private UiDebounce? _searchDebounce;

    /// <summary><para>是否自动滚动至最新条目。Whether to auto-scroll to the latest entry.</para></summary>
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary><para>级别筛选。<c>null</c> 表示全部级别。Level filter; <c>null</c> means all levels.</para></summary>
    [ObservableProperty] private LogLevel? _levelFilter;

    /// <summary><para>来源筛选。空字符串表示全部。Source filter; empty means all.</para></summary>
    [ObservableProperty] private string _sourceFilter = string.Empty;

    /// <summary><para>忽略大小写的完整日志行搜索，空字符串表示全部。</para>Case-insensitive full-line search; empty means all.</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        (_searchDebounce ??= new UiDebounce(ReapplyFilter)).Restart();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary><para>全部级别选项。All level options.</para></summary>
    public IReadOnlyList<LogLevel?> Levels { get; } = [null, LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal];

    /// <summary><para>全部来源名称（去重）。All source names (deduplicated).</para></summary>
    public ObservableCollection<string> Sources { get; } = [string.Empty];

    partial void OnLevelFilterChanged(LogLevel? value) => ReapplyFilter();
    partial void OnSourceFilterChanged(string value) => ReapplyFilter();

    public WorkspaceLogViewModel() : this(true) { }

    internal WorkspaceLogViewModel(bool live)
    {
        if (live)
        {
            _unsubscribe = LogService.Instance.Subscribe(OnLogEnqueued);
            _flushTimer.Tick += (_, _) => FlushPending();
            _flushTimer.Start();
            foreach (var entry in LogService.Instance.Entries)
            {
                Enqueue(entry);
            }
        }
        FlushPending();
    }

    /// <summary>
    /// <para>日志条目入队回调：仅加锁追加到待刷新队列，由 <see cref="_flushTimer"/> 每 100ms
    /// 在 UI 线程批量消费，避免日志风暴时每条一次 Dispatcher.Post。</para>
    /// Log entry enqueue callback: only lock-appends to the pending backlog; the flush
    /// timer drains it on the UI thread every 100 ms, avoiding one Dispatcher.Post per
    /// entry during log storms.
    /// </summary>
    private void OnLogEnqueued(object? sender, LogEntry entry)
        => Enqueue(entry);

    internal void Enqueue(LogEntry entry)
    {
        var channel = LogService.ChannelOf(entry.Source);
        if (channel != LogChannel.Firmware && channel != LogChannel.Flash)
        {
            return;
        }

        lock (_pendingGate)
        {
            if (_disposed)
            {
                return;
            }
            _pendingEntries.Enqueue(entry);
            _pendingCharacters += (long)entry.Message.Length + entry.Source.Length;

            while (_pendingEntries.Count > MaxEntries || _pendingCharacters > MaximumCharacters)
            {
                var removed = _pendingEntries.Dequeue();
                _pendingCharacters -= (long)removed.Message.Length + removed.Source.Length;
            }
        }
    }

    /// <summary>
    /// <para>按条数与字符预算分批刷新，只淘汰筛选视图中实际过期的条目。</para>
    /// Flushes a bounded batch and removes only expired entries actually present in the filtered view.
    /// </summary>
    internal void FlushPending()
    {
        if (_disposed)
        {
            return;
        }
        List<LogEntry> batch;
        lock (_pendingGate)
        {
            if (_pendingEntries.Count == 0)
            {
                return;
            }
            batch = new List<LogEntry>(BatchEntries);
            long characters = 0;
            while (_pendingEntries.TryPeek(out var next) && batch.Count < BatchEntries)
            {
                var length = (long)next.Message.Length + next.Source.Length;

                if (batch.Count > 0 && characters + length > BatchCharacters)
                {
                    break;
                }
                batch.Add(_pendingEntries.Dequeue());
                characters += length;
                _pendingCharacters -= length;
            }
        }
        if (batch.Count == 0)
        {
            return;
        }
        var previousCount = _allEntries.Count;
        var added = new List<LogEntryViewModel>(batch.Count);
        foreach (var entry in batch)
        {
            TrackEntry(entry);
        }
        var overflow = 0;
        var visibleRemoved = 0;
        while (_allEntries.Count - overflow > MaxEntries || _historyCharacters > MaximumCharacters)
        {
            var removed = _allEntries[overflow];
            _historyCharacters -= removed.Text.Length;

            if (overflow < previousCount && visibleRemoved < Entries.Count
                && ReferenceEquals(Entries[visibleRemoved], removed))
            {
                visibleRemoved++;
            }
            if (--_sourceCounts[removed.Source] == 0)
            {
                _sourceCounts.Remove(removed.Source);
                if (removed.Source.Length != 0)
                {
                    Sources.Remove(removed.Source);
                }
            }
            overflow++;
        }
        for (var i = Math.Max(previousCount, overflow); i < _allEntries.Count; i++)
        {
            if (MatchesFilter(_allEntries[i]))
            {
                added.Add(_allEntries[i]);
            }
        }
        _allEntries.RemoveRange(0, overflow);
        Entries.Apply(visibleRemoved, added);
    }

    /// <summary>
    /// <para>登记一条日志到原始缓冲与组件名集合，不触碰展示集合。</para>
    /// Tracks a log entry in the raw buffer and source names without touching the display collection.
    /// </summary>
    private LogEntryViewModel TrackEntry(LogEntry entry)
    {
        var vm = new LogEntryViewModel(
            entry.Timestamp.ToString("HH:mm:ss"),
            entry.Level,
            entry.Source,
            entry.Message);

        _allEntries.Add(vm);
        _historyCharacters += vm.Text.Length;
        var sourceCount = _sourceCounts.GetValueOrDefault(entry.Source);
        _sourceCounts[entry.Source] = sourceCount + 1;
        if (sourceCount == 0 && entry.Source.Length != 0)
        {
            Sources.Add(entry.Source);
        }

        return vm;
    }

    private bool MatchesFilter(LogEntryViewModel vm)
    {
        if (LevelFilter.HasValue && vm.Level != LevelFilter.Value) return false;
        if (!string.IsNullOrEmpty(SourceFilter) && !string.Equals(vm.Source, SourceFilter, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(SearchText) && !vm.Text.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void ReapplyFilter()
    {

        List<LogEntryViewModel> hits;
        if (LevelFilter is null && string.IsNullOrEmpty(SourceFilter) && string.IsNullOrEmpty(SearchText))
        {
            hits = _allEntries;
        }
        else
        {
            hits = [];
            foreach (var e in _allEntries)
            {
                if (MatchesFilter(e)) hits.Add(e);
            }
        }

        Entries.Replace(hits);
    }

    /// <summary><para>清空全部日志条目。Clears all log entries.</para></summary>
    public void Clear()
    {
        lock (_pendingGate)
        {
            _pendingEntries.Clear();
            _pendingCharacters = 0;
        }
        _allEntries.Clear();
        _historyCharacters = 0;
        Entries.Clear();
        Sources.Clear();
        _sourceCounts.Clear();
        Sources.Add(string.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_pendingGate)
        {
            _disposed = true;
        }
        _flushTimer.Stop();
        _searchDebounce?.Stop();
        _unsubscribe?.Invoke();
        _unsubscribe = null;
        Clear();
        GC.SuppressFinalize(this);
    }
}