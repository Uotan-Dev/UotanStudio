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
            // 显示与搜索共用同一格式化结果，避免筛选时每条日志重复格式化。
            // Display and search share one formatted string; no reformatting per filter pass.
            Text = $"{timestamp} [{level,-5}] {source} - {message}".ReplaceLineEndings("\n");
        }
    }

    /// <summary><para>当前展示的日志条目集合（已按级别与来源筛选）。Collection of displayed log entries (filtered).</para></summary>
    public LogEntryCollection<LogEntryViewModel> Entries { get; } = [];

    private readonly List<LogEntryViewModel> _allEntries = [];
    private const int MaxEntries = 5000;
    private Action? _unsubscribe;
    private bool _disposed;

    /// <summary><para>待刷新日志队列锁。</para>Gate for the pending log backlog.</para></summary>
    private readonly object _pendingGate = new();
    private readonly List<LogEntry> _pendingEntries = [];
    private readonly HashSet<string> _sourceSet = new(StringComparer.Ordinal);

    /// <summary><para>100ms 批量刷新定时器，与全局日志面板共用同一批量约定。</para>100 ms flush timer, sharing the batched convention with the global log panel.</para></summary>
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DispatcherTimer? _searchDebounce;

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
        // 搜索输入 250ms 防抖：避免每次按键都全量重建展示集合（与全局日志面板一致）。
        // 250 ms debounce for search input: avoids rebuilding the display collection
        // on every keystroke (matching the global log panel).
        if (_searchDebounce is null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce?.Stop();
                ReapplyFilter();
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary><para>全部级别选项。All level options.</para></summary>
    public IReadOnlyList<LogLevel?> Levels { get; } = [null, LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal];

    /// <summary><para>全部来源名称（去重）。All source names (deduplicated).</para></summary>
    public ObservableCollection<string> Sources { get; } = [string.Empty];

    partial void OnLevelFilterChanged(LogLevel? value) => ReapplyFilter();
    partial void OnSourceFilterChanged(string value) => ReapplyFilter();

    public WorkspaceLogViewModel()
    {
        _unsubscribe = LogService.Instance.Subscribe(OnLogEnqueued);
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();
        foreach (var e in LogService.Instance.Entries)
        {
            // 历史回填与实时订阅同一通道范围：仅固件/烧录通道。
            // History backfill honors the same channel scope as the live subscription.
            var channel = LogService.ChannelOf(e.Source);
            if (channel is LogChannel.Firmware or LogChannel.Flash)
            {
                TrackEntry(e);
            }
        }

        // 历史回填走整体重建（单次 Reset），不走逐条 Add。
        // History backfill goes through one bulk rebuild (single Reset) instead of per-item Adds.
        ReapplyFilter();
    }

    /// <summary>
    /// <para>日志条目入队回调：仅加锁追加到待刷新队列，由 <see cref="_flushTimer"/> 每 100ms
    /// 在 UI 线程批量消费，避免日志风暴时每条一次 Dispatcher.Post。</para>
    /// Log entry enqueue callback: only lock-appends to the pending backlog; the flush
    /// timer drains it on the UI thread every 100 ms, avoiding one Dispatcher.Post per
    /// entry during log storms.
    /// </summary>
    private void OnLogEnqueued(object? sender, LogEntry entry)
    {
        var channel = LogService.ChannelOf(entry.Source);
        if (channel != LogChannel.Firmware && channel != LogChannel.Flash)
        {
            return;
        }

        lock (_pendingGate)
        {
            _pendingEntries.Add(entry);
        }
    }

    /// <summary>
    /// <para>定时批量刷新：一次性把积压日志追加到集合并批量裁剪超限条目。</para>
    /// Timed batch flush: appends the whole backlog at once and trims over-limit entries in bulk.
    /// </summary>
    private void FlushPending()
    {
        List<LogEntry> batch;
        lock (_pendingGate)
        {
            if (_pendingEntries.Count == 0)
            {
                return;
            }
            batch = new List<LogEntry>(_pendingEntries);
            _pendingEntries.Clear();
        }

        // 一批日志先统一登记，再按命中筛选一次性追加（最多两次集合事件）。
        // Track the whole batch first, then append all filter hits in one Apply
        // (at most two collection events).
        var added = new List<LogEntryViewModel>(batch.Count);
        foreach (var entry in batch)
        {
            var vm = TrackEntry(entry);
            if (MatchesFilter(vm))
            {
                added.Add(vm);
            }
        }

        Entries.Apply(0, added);

        // 批量裁剪：一次移除全部超限条目，替代逐条 Remove 的 O(n) 查找。
        // Bulk trim: remove all over-limit entries in one pass instead of per-entry Remove.
        var overflow = _allEntries.Count - MaxEntries;
        if (overflow <= 0)
        {
            return;
        }
        var removed = _allEntries.Take(overflow).ToArray();
        Entries.Apply(removed.Length, Array.Empty<LogEntryViewModel>());
        _allEntries.RemoveRange(0, overflow);
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

        if (_sourceSet.Add(entry.Source))
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
        // 整体重建（单次 Reset），替代 Clear + 逐条 Add 的 5000 次集合事件。
        // One bulk rebuild (single Reset) instead of Clear plus 5000 per-item Adds.
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
        _allEntries.Clear();
        Entries.Clear();
        Sources.Clear();
        _sourceSet.Clear();
        Sources.Add(string.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Stop();
        _searchDebounce?.Stop();
        _unsubscribe?.Invoke();
        _unsubscribe = null;
        GC.SuppressFinalize(this);
    }
}
