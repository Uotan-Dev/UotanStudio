using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>工作区日志面板视图模型，订阅统一日志服务并过滤显示固件（Firmware）与构建（Build）通道的日志条目。
/// 专用于解包/打包、固件操作、工作流执行等日志的实时展示。</para>
/// Workspace log panel view model, subscribing to the unified log service and filtering for
/// Firmware and Build channel log entries. Dedicated to real-time display of unpack/pack,
/// firmware operations, and workflow execution logs.
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

        public LogEntryViewModel(string timestamp, LogLevel level, string source, string message)
        {
            _timestamp = timestamp;
            _level = level;
            _source = source;
            _message = message;
        }
    }

    /// <summary><para>当前展示的日志条目集合（已按级别与来源筛选）。Collection of displayed log entries (filtered).</para></summary>
    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];

    private readonly List<LogEntryViewModel> _allEntries = [];
    private const int MaxEntries = 5000;
    private Action? _unsubscribe;
    private bool _disposed;

    /// <summary><para>是否自动滚动至最新条目。Whether to auto-scroll to the latest entry.</para></summary>
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary><para>级别筛选。<c>null</c> 表示全部级别。Level filter; <c>null</c> means all levels.</para></summary>
    [ObservableProperty] private LogLevel? _levelFilter;

    /// <summary><para>来源筛选。空字符串表示全部。Source filter; empty means all.</para></summary>
    [ObservableProperty] private string _sourceFilter = string.Empty;

    /// <summary><para>全部级别选项。All level options.</para></summary>
    public IReadOnlyList<LogLevel?> Levels { get; } = [null, LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal];

    /// <summary><para>全部来源名称（去重）。All source names (deduplicated).</para></summary>
    public ObservableCollection<string> Sources { get; } = [string.Empty];

    partial void OnLevelFilterChanged(LogLevel? value) => ReapplyFilter();
    partial void OnSourceFilterChanged(string value) => ReapplyFilter();

    public WorkspaceLogViewModel()
    {
        _unsubscribe = LogService.Instance.Subscribe(OnLogEnqueued);
        foreach (var e in LogService.Instance.Entries)
        {
            OnLogEnqueued(this, e);
        }
    }

    private void OnLogEnqueued(object? sender, LogEntry entry)
    {
        var channel = LogService.ChannelOf(entry.Source);
        if (channel != LogChannel.Firmware && channel != LogChannel.Build)
        {
            return;
        }

        var vm = new LogEntryViewModel(
            entry.Timestamp.ToString("HH:mm:ss"),
            entry.Level,
            entry.Source,
            entry.Message);

        _allEntries.Add(vm);

        while (_allEntries.Count > MaxEntries)
        {
            _allEntries.RemoveAt(0);
        }

        if (!Sources.Contains(entry.Source))
        {
            Sources.Add(entry.Source);
        }

        if (MatchesFilter(vm))
        {
            Entries.Add(vm);
        }
    }

    private bool MatchesFilter(LogEntryViewModel vm)
    {
        if (LevelFilter.HasValue && vm.Level != LevelFilter.Value) return false;
        if (!string.IsNullOrEmpty(SourceFilter) && !string.Equals(vm.Source, SourceFilter, StringComparison.Ordinal)) return false;
        return true;
    }

    private void ReapplyFilter()
    {
        Entries.Clear();
        foreach (var e in _allEntries)
        {
            if (MatchesFilter(e)) Entries.Add(e);
        }
    }

    /// <summary><para>清空全部日志条目。Clears all log entries.</para></summary>
    public void Clear()
    {
        _allEntries.Clear();
        Entries.Clear();
        Sources.Clear();
        Sources.Add(string.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _unsubscribe?.Invoke();
        _unsubscribe = null;
        GC.SuppressFinalize(this);
    }
}
