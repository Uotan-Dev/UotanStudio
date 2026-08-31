using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>全局日志面板视图模型，订阅 <see cref="LogService"/> 日志条目推送并维护可绑定集合，供 LogView 显示。</para>
/// Global log panel view model, subscribing to <see cref="LogService"/> log entry pushes and maintaining a bindable collection for the LogView to display.
/// </summary>
public partial class LogViewModel : ViewModelBase
{
    /// <summary>
    /// <para>日志条目视图模型，承载单条日志的时间戳、级别、来源与文本，并提供级别前景色。</para>
    /// Log entry view model, carrying a single log entry's timestamp, level, source and text, plus a level foreground brush key.
    /// </summary>
    public sealed partial class LogEntryViewModel : ObservableObject
    {
        /// <summary><para>本地时间戳文本（HH:mm:ss）。</para>Local timestamp text (HH:mm:ss).</summary>
        [ObservableProperty] private string _timestamp = string.Empty;

        /// <summary><para>日志级别。</para>Log level.</summary>
        [ObservableProperty] private LogLevel _level = LogLevel.Info;

        /// <summary><para>日志来源。</para>Log source.</summary>
        [ObservableProperty] private string _source = string.Empty;

        /// <summary><para>日志文本。</para>Log text.</summary>
        [ObservableProperty] private string _message = string.Empty;

        /// <summary>
        /// <para>创建日志条目视图模型。</para>
        /// Creates a log entry view model.
        /// </summary>
        public LogEntryViewModel(string timestamp, LogLevel level, string source, string message)
        {
            _timestamp = timestamp;
            _level = level;
            _source = source;
            _message = message;
        }
    }

    /// <summary>
    /// <para>通道筛选选项，承载通道值与本地化显示文本；<see cref="Value"/> 为 <c>null</c> 表示"全部通道"。</para>
    /// Channel filter option, carrying a channel value and its localized display text; a <c>null</c> <see cref="Value"/> means "all channels".
    /// </summary>
    public sealed partial class LogChannelOption : ObservableObject
    {
        /// <summary><para>通道值；<c>null</c> 表示"全部通道"。</para>Channel value; <c>null</c> means "all channels".</summary>
        public LogChannel? Value { get; }

        /// <summary><para>本地化显示文本。</para>Localized display text.</summary>
        [ObservableProperty] private string _display = string.Empty;

        /// <summary>
        /// <para>创建通道筛选选项。</para>
        /// Creates a channel filter option.
        /// </summary>
        public LogChannelOption(LogChannel? value)
        {
            Value = value;
            _display = Localize(value);
        }

        /// <summary>
        /// <para>语言切换后刷新本地化显示文本。</para>
        /// Refreshes the localized display text after a language change.
        /// </summary>
        public void RefreshDisplay() => Display = Localize(Value);

        private static string Localize(LogChannel? value) =>
            value is null
                ? LanguageService.Instance.GetString("Log_Channel_All")
                : LanguageService.Instance.GetString("Log_Channel_" + value.Value);
    }

    /// <summary>
    /// <para>当前日志面板的展示条目集合（已按级别、组件与通道筛选），UI 绑定此项。</para>
    /// Collection of entries currently displayed in the log panel (filtered by level, source and channel),
    /// bound by the UI.
    /// </summary>
    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// <para>全部日志条目的原始缓冲（未筛选），超出上限时丢弃最旧条目。</para>
    /// Raw buffer of all log entries (unfiltered); the oldest entries are dropped when over the cap.
    /// </summary>
    private readonly List<LogEntryViewModel> _allEntries = [];

    /// <summary>
    /// <para>累计条目数上限，超过后丢弃最旧条目以防止无限增长。</para>
    /// Cumulative entry cap; the oldest entries are dropped when exceeded to prevent unbounded growth.
    /// </summary>
    private const int MaxEntries = 5000;

    /// <summary>
    /// <para>取消订阅委托。</para>
    /// Unsubscribe delegate.
    /// </summary>
    private Action? _unsubscribe;

    /// <summary>
    /// <para>是否自动滚动至最新条目。</para>
    /// Whether to auto-scroll to the latest entry.
    /// </summary>
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary>
    /// <para>级别筛选：仅显示不低于此级别的日志；<c>null</c> 表示全部级别（默认）。</para>
    /// Level filter: only logs at or above this level are shown; <c>null</c> means all levels
    /// (the default).
    /// </summary>
    [ObservableProperty] private LogLevel? _levelFilter;

    /// <summary>
    /// <para>组件筛选：仅显示指定来源的日志；空字符串表示全部组件（默认）。</para>
    /// Source filter: only logs from the selected source are shown; empty means all sources (the default).
    /// </summary>
    [ObservableProperty] private string _sourceFilter = string.Empty;

    /// <summary>
    /// <para>当前日志中出现的全部组件名（去重），首项为空字符串表示"全部组件"，
    /// 供组件筛选下拉框绑定。</para>
    /// All source names seen in the log (deduplicated); the leading empty string represents
    /// "all sources", bound by the source-filter combo box.
    /// </summary>
    public ObservableCollection<string> Sources { get; } = [string.Empty];

    /// <summary>
    /// <para>全部日志级别（首项 <c>null</c> 表示"全部级别"），供级别筛选下拉框绑定。</para>
    /// All log levels (leading <c>null</c> means "all levels"), bound by the level-filter combo box.
    /// </summary>
    public IReadOnlyList<LogLevel?> Levels { get; } = [null, LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal];

    partial void OnLevelFilterChanged(LogLevel? value) => ReapplyFilter();

    partial void OnSourceFilterChanged(string value) => ReapplyFilter();

    /// <summary>
    /// <para>通道筛选选项集合（五个通道 + 首项"全部通道"），供日志面板通道 Tab 绑定。</para>
    /// Channel filter options (five channels plus a leading "all channels" item), bound by the channel tabs in the log panel.
    /// </summary>
    public ObservableCollection<LogChannelOption> Channels { get; } =
    [
        new(null),
        new(LogChannel.General),
        new(LogChannel.Device),
        new(LogChannel.Flash),
        new(LogChannel.Firmware),
        new(LogChannel.Build),
    ];

    /// <summary>
    /// <para>选中的通道筛选选项；其 <see cref="LogChannelOption.Value"/> 为 <c>null</c> 表示全部通道（默认）。
    /// 按 <see cref="LogService.ChannelOf"/> 与级别、组件筛选叠加生效。</para>
    /// The selected channel filter option; a <see cref="LogChannelOption.Value"/> of <c>null</c> means all channels
    /// (the default). It is combined with the level and source filters via <see cref="LogService.ChannelOf"/>.
    /// </summary>
    [ObservableProperty] private LogChannelOption? _selectedChannel;

    partial void OnSelectedChannelChanged(LogChannelOption? value) => ReapplyFilter();

    /// <summary>
    /// <para>构造日志面板视图模型，订阅统一日志服务与语言变更并回填历史缓冲。</para>
    /// Constructs the log panel view model, subscribing to the unified log service and language changes and backfilling the historical buffer.
    /// </summary>
    public LogViewModel()
    {
        SelectedChannel = Channels[0];
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
        _unsubscribe = LogService.Instance.Subscribe(OnLogEnqueued);
        foreach (var e in LogService.Instance.Entries)
        {
            AppendEntry(e);
        }
    }

    /// <summary>
    /// <para>日志条目入队回调，追加到集合并裁剪超限条目；跨线程调用时调度到 UI 线程，
    /// 确保 <see cref="ObservableCollection{T}"/> 的变更总能触发界面刷新。</para>
    /// Log entry enqueue callback, appending to the collection and trimming over-limit entries;
    /// cross-thread calls are dispatched to the UI thread so collection changes always refresh the view.
    /// </summary>
    /// <param name="sender"><para>事件源。</para>Event source.</param>
    /// <param name="entry"><para>日志条目记录。</para>Log entry record.</param>
    private void OnLogEnqueued(object? sender, LogEntry entry)
    {
        void AppendAndTrim()
        {
            AppendEntry(entry);
            while (_allEntries.Count > MaxEntries && _allEntries.Count > 0)
            {
                var removed = _allEntries[0];
                _allEntries.RemoveAt(0);
                Entries.Remove(removed);
            }
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            AppendAndTrim();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(AppendAndTrim);
        }
    }

    /// <summary>
    /// <para>追加一条日志到原始缓冲；命中当前筛选时同步加入展示集合，并登记组件名。</para>
    /// Appends a log entry to the raw buffer; adds it to the display collection when it matches the
    /// current filters, and records the source name.
    /// </summary>
    private void AppendEntry(LogEntry entry)
    {
        var ts = entry.Timestamp.ToString("HH:mm:ss");
        var vm = new LogEntryViewModel(ts, entry.Level, entry.Source, entry.Message);
        _allEntries.Add(vm);

        if (!Sources.Contains(entry.Source))
        {
            Sources.Add(entry.Source);
        }

        if (MatchesFilter(vm))
        {
            Entries.Add(vm);
        }
    }

    /// <summary>
    /// <para>判断条目是否命中当前级别、组件与通道筛选。</para>
    /// Determines whether the entry matches the current level, source and channel filters.
    /// </summary>
    private bool MatchesFilter(LogEntryViewModel vm) =>
        (LevelFilter is null || vm.Level >= LevelFilter.Value)
        && (string.IsNullOrEmpty(SourceFilter) || string.Equals(vm.Source, SourceFilter, StringComparison.Ordinal))
        && (SelectedChannel?.Value is null || LogService.ChannelOf(vm.Source) == SelectedChannel.Value.Value);

    /// <summary>
    /// <para>按当前筛选条件重建展示集合。</para>
    /// Rebuilds the display collection according to the current filters.
    /// </summary>
    private void ReapplyFilter()
    {
        Entries.Clear();
        foreach (var vm in _allEntries)
        {
            if (MatchesFilter(vm))
            {
                Entries.Add(vm);
            }
        }
    }

    /// <summary>
    /// <para>清空当前日志集合（不影响 LogService 缓冲）。</para>
    /// Clears the current log collection (does not affect the LogService buffer).
    /// </summary>
    public void Clear()
    {
        _allEntries.Clear();
        Entries.Clear();
        Sources.Clear();
        Sources.Add(string.Empty);
    }

    /// <summary>
    /// <para>语言变更时刷新通道 Tab 的本地化显示文本。</para>
    /// Refreshes the localized channel tab text when the language changes.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var option in Channels)
        {
            option.RefreshDisplay();
        }
    }

    /// <summary>
    /// <para>析构时取消订阅，防止内存泄漏。</para>
    /// Unsubscribes on finalization to prevent memory leaks.
    /// </summary>
    ~LogViewModel()
    {
        try
        {
            _unsubscribe?.Invoke();
            LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        }
        catch { /* 忽略析构异常 */ }
    }
}