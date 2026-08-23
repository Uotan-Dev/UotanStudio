using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>全局日志面板视图模型：订阅统一日志服务（<see cref="LogService"/>）的条目推送并维护
/// 可过滤的绑定集合；同时提供设备 logcat 捕获（捕获的设备日志同样汇入统一日志流）。</para>
/// Global log panel view model: subscribes to the unified log service
/// (<see cref="LogService"/>) and maintains a filterable bindable collection; also
/// provides device logcat capture (captured device logs feed the unified stream too).
/// </summary>
public partial class LogViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// <para>累计条目上限，超过后丢弃最旧条目以防止无限增长。</para>
    /// Cumulative entry cap; the oldest entries are dropped when exceeded.
    /// </summary>
    private const int MaxEntries = 5000;

    private readonly Action? _unsubscribe;
    private CancellationTokenSource? _captureCts;
    private string? _activeSerial;

    /// <summary>
    /// <para>过滤文本后备字段。</para>
    /// Filter text backing field.
    /// </summary>
    [ObservableProperty]
    private string _filter = string.Empty;

    /// <summary>
    /// <para>级别过滤后备字段（如 W/E，空表示不过滤）。</para>
    /// Level filter backing field (e.g. W/E; empty means no filter).
    /// </summary>
    [ObservableProperty]
    private string _levelFilter = string.Empty;

    /// <summary>
    /// <para>是否自动滚动后备字段。</para>
    /// Auto-scroll backing field.
    /// </summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>
    /// <para>是否正在捕获后备字段。</para>
    /// Is-capturing backing field.
    /// </summary>
    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>
    /// <para>是否已暂停捕获后备字段。</para>
    /// Is-paused backing field.
    /// </summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>
    /// <para>状态文本后备字段。</para>
    /// Status text backing field.
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// <para>获取日志条目集合（全量，含被过滤条目）。</para>
    /// Gets the log entry collection (unfiltered).
    /// </summary>
    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// <para>获取经过滤后的日志条目集合（供 UI 绑定）。</para>
    /// Gets the filtered log entry collection (for UI binding).
    /// </summary>
    public ObservableCollection<LogEntryViewModel> FilteredEntries { get; } = [];

    /// <summary>
    /// <para>构造日志面板视图模型：订阅统一日志服务并回填历史缓冲。</para>
    /// Constructs the log panel view model, subscribing to the unified log service and
    /// backfilling the historical buffer.
    /// </summary>
    public LogViewModel()
    {
        _unsubscribe = LogService.Instance.Subscribe(OnLogEnqueued);
        foreach (var e in LogService.Instance.Entries)
        {
            AppendEntry(e);
        }

        RefreshFilter();
    }

    /// <summary>
    /// <para>日志条目入队回调：切换到 UI 线程追加条目并裁剪超限条目。</para>
    /// Log enqueue callback: marshals to the UI thread, appends the entry and trims
    /// over-limit entries.
    /// </summary>
    private void OnLogEnqueued(object? sender, LogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AppendEntry(entry);
            while (Entries.Count is > MaxEntries and > 0)
            {
                Entries.RemoveAt(0);
            }

            if (!IsPaused)
            {
                RefreshFilterTail();
            }
        });
    }

    /// <summary>
    /// <para>追加一条日志到集合（不做过滤）。</para>
    /// Appends a log entry to the collection (no filtering).
    /// </summary>
    private void AppendEntry(LogEntry entry)
    {
        Entries.Add(new LogEntryViewModel(
            entry.Timestamp.ToString("HH:mm:ss.fff"),
            entry.Level,
            entry.Source,
            entry.Message));
    }

    partial void OnFilterChanged(string value) => RefreshFilter();

    partial void OnLevelFilterChanged(string value) => RefreshFilter();

    /// <summary>
    /// <para>根据当前过滤文本与级别过滤重建过滤集合。</para>
    /// Rebuilds the filtered collection from the current text and level filters.
    /// </summary>
    private void RefreshFilter()
    {
        var keyword = Filter?.Trim() ?? string.Empty;
        var level = LevelFilter?.Trim() ?? string.Empty;
        var matched = Entries.Where(e => MatchesFilter(e, keyword, level)).ToList();
        FilteredEntries.Clear();
        foreach (var e in matched)
        {
            FilteredEntries.Add(e);
        }
    }

    /// <summary>
    /// <para>增量过滤：仅在条目匹配时追加到过滤集合尾部（避免全量重建）。</para>
    /// Incremental filtering: appends the entry to the filtered collection only when it
    /// matches (avoids a full rebuild).
    /// </summary>
    private void RefreshFilterTail()
    {
        var keyword = Filter?.Trim() ?? string.Empty;
        var level = LevelFilter?.Trim() ?? string.Empty;
        if (Entries.Count == 0)
        {
            return;
        }

        var last = Entries[^1];
        if (MatchesFilter(last, keyword, level) &&
            (FilteredEntries.Count == 0 || !ReferenceEquals(FilteredEntries[^1], last)))
        {
            FilteredEntries.Add(last);
        }
    }

    private static bool MatchesFilter(LogEntryViewModel e, string keyword, string level)
    {
        var keywordOk = string.IsNullOrEmpty(keyword)
            || (e.Message?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.Source?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.Timestamp?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
        var levelOk = string.IsNullOrEmpty(level)
            || e.Level.ToString().Contains(level, StringComparison.OrdinalIgnoreCase)
            || LevelShort(e.Level).Contains(level, StringComparison.OrdinalIgnoreCase);
        return keywordOk && levelOk;
    }

    /// <summary>
    /// <para>获取级别的单字母缩写（T/D/I/W/E/F），与 logcat 级别字符一致。</para>
    /// Gets the single-letter level abbreviation (T/D/I/W/E/F), matching logcat level chars.
    /// </summary>
    public static string LevelShort(LogLevel level) => level switch
    {
        LogLevel.Trace => "T",
        LogLevel.Debug => "D",
        LogLevel.Info => "I",
        LogLevel.Warn => "W",
        LogLevel.Error => "E",
        LogLevel.Fatal => "F",
        _ => "I",
    };

    /// <summary>
    /// <para>设置当前激活的设备序列号，供 logcat 捕获使用。</para>
    /// Sets the currently active device serial for logcat capture.
    /// </summary>
    public void SetActiveDevice(string? serial) => _activeSerial = serial;

    /// <summary>
    /// <para>开始捕获日志：打开设备（或网络）ADB 会话，将 logcat 输出解析后汇入统一日志流。</para>
    /// Starts log capture: opens a device (or network) ADB session and feeds parsed
    /// logcat output into the unified log stream.
    /// </summary>
    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        if (IsCapturing)
        {
            return;
        }

        if (string.IsNullOrEmpty(_activeSerial))
        {
            StatusText = LanguageService.Instance.GetString("Log_SelectDeviceFirst");
            return;
        }

        var device = DeviceMonitorService.Instance.FindDeviceBySerial(_activeSerial);
        if (device is null || device.Protocol != DeviceProtocolKind.Adb)
        {
            StatusText = LanguageService.Instance.GetString("Log_SelectDeviceFirst");
            return;
        }

        StatusText = LanguageService.Instance.GetString("Log_CaptureStarting");
        IsCapturing = true;
        IsPaused = false;
        _captureCts?.Cancel();
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;

        LogService.Instance.Write(LogLevel.Info, "Log",
            LanguageService.Instance.GetString("Log_CaptureStarted") + $" [{device.Serial}]");
        StatusText = LanguageService.Instance.GetString("Log_CaptureStarted");

        try
        {
            await DeviceMonitorService.Instance.StartLogcatStreamingAsync(device, line =>
            {
                if (IsPaused)
                {
                    return;
                }

                (var level, var tag, var message) = ParseLogcatLine(line);
                LogService.Instance.Write(level, tag, message);
            }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 正常取消路径
        }
        finally
        {
            IsCapturing = false;
            StatusText = LanguageService.Instance.GetString("Log_CaptureStopped");
        }
    }

    /// <summary>
    /// <para>停止捕获日志。</para>
    /// Stops capturing logs.
    /// </summary>
    [RelayCommand]
    private void StopCapture()
    {
        _captureCts?.Cancel();
        IsCapturing = false;
        IsPaused = false;
        LogService.Instance.Write(LogLevel.Info, "Log", LanguageService.Instance.GetString("Log_CaptureStopped"));
        StatusText = LanguageService.Instance.GetString("Log_CaptureStopped");
    }

    /// <summary>
    /// <para>暂停捕获（捕获流继续，但条目不再汇入统一日志）。</para>
    /// Pauses capture (the stream continues, but entries no longer feed the unified log).
    /// </summary>
    [RelayCommand]
    private void PauseCapture()
    {
        if (!IsCapturing)
        {
            return;
        }

        IsPaused = true;
        StatusText = LanguageService.Instance.GetString("Log_Paused");
    }

    /// <summary>
    /// <para>继续捕获（从暂停状态恢复）。</para>
    /// Resumes capture (from the paused state).
    /// </summary>
    [RelayCommand]
    private void ResumeCapture()
    {
        if (!IsCapturing)
        {
            return;
        }

        IsPaused = false;
        StatusText = LanguageService.Instance.GetString("Log_Resumed");
    }

    /// <summary>
    /// <para>清空当前日志集合与统一日志服务缓冲。</para>
    /// Clears the current log collection and the unified log service buffer.
    /// </summary>
    [RelayCommand]
    private void ClearLogs()
    {
        Entries.Clear();
        FilteredEntries.Clear();
        LogService.Instance.Clear();
    }

    /// <summary>
    /// <para>将当前日志条目导出到指定文件。</para>
    /// Exports current log entries to the specified file path.
    /// </summary>
    /// <param name="filePath">The target file path.</param>
    /// <returns>Whether the export succeeded.</returns>
    public async Task<bool> ExportLogsAsync(string filePath)
    {
        try
        {
            await using var writer = new StreamWriter(filePath, append: false);
            foreach (var entry in Entries)
            {
                await writer.WriteLineAsync(
                    $"{entry.Timestamp} [{LevelShort(entry.Level)}] [{entry.Source}] {entry.Message}").ConfigureAwait(false);
            }
            StatusText = string.Format(LanguageService.Instance.GetString("Log_ExportSucceeded"), filePath);
            return true;
        }
        catch
        {
            StatusText = LanguageService.Instance.GetString("Log_ExportFailed");
            return false;
        }
    }

    /// <summary>
    /// <para>解析单行 logcat 输出为（级别、标签、消息）三元组。</para>
    /// Parses a single logcat output line into a (level, tag, message) triple.
    /// </summary>
    private static (LogLevel Level, string Tag, string message) ParseLogcatLine(string line)
    {
        // logcat 格式: "MM-DD HH:MM:SS.UUU PID TID LEVEL TAG: MESSAGE"
        var level = LogLevel.Info;
        var tag = "logcat";
        var message = line;
        if (string.IsNullOrEmpty(line))
        {
            return (level, tag, message);
        }

        for (var i = 1; i < line.Length - 1; i++)
        {
            if (line[i] == ' ' && "VDIWEF".IndexOf(line[i + 1]) >= 0
                && i + 2 < line.Length && line[i + 2] == '/')
            {
                level = line[i + 1] switch
                {
                    'V' => LogLevel.Trace,
                    'D' => LogLevel.Debug,
                    'W' => LogLevel.Warn,
                    'E' => LogLevel.Error,
                    'F' => LogLevel.Fatal,
                    _ => LogLevel.Info,
                };
                var tagStart = i + 3;
                var tagEnd = line.IndexOf(':', tagStart);
                if (tagEnd > tagStart)
                {
                    tag = line[tagStart..tagEnd].Trim();
                    message = tagEnd + 1 < line.Length
                        ? line[(tagEnd + 1)..].TrimStart()
                        : string.Empty;
                }
                break;
            }
        }
        return (level, tag, message);
    }

    /// <summary>
    /// <para>取消订阅统一日志服务。</para>
    /// Unsubscribes from the unified log service.
    /// </summary>
    public void Dispose()
    {
        _unsubscribe?.Invoke();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// <para>日志条目视图模型，承载单条日志的时间戳、级别、来源与文本，并提供级别前景色。</para>
/// Log entry view model, carrying a single log entry's timestamp, level, source and text,
/// plus a level foreground brush.
/// </summary>
/// <remarks>
/// <para>创建日志条目视图模型。</para>
/// Creates a log entry view model.
/// </remarks>
public sealed partial class LogEntryViewModel(string timestamp, LogLevel level, string source, string message) : ObservableObject
{
    private static readonly IBrush TraceBrush = new SolidColorBrush(Color.Parse("#9E9E9E"));
    private static readonly IBrush DebugBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#81C784"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E57373"));
    private static readonly IBrush FatalBrush = new SolidColorBrush(Color.Parse("#BA68C8"));

    /// <summary><para>本地时间戳文本（HH:mm:ss.fff）。</para>Local timestamp text.</summary>
    public string Timestamp { get; } = timestamp;

    /// <summary><para>日志级别。</para>Log level.</summary>
    public LogLevel Level { get; } = level;

    /// <summary><para>日志来源。</para>Log source.</summary>
    public string Source { get; } = source;

    /// <summary><para>日志文本。</para>Log text.</summary>
    public string Message { get; } = message;

    /// <summary>
    /// <para>获取级别对应的显示文本（缩写 + 名称）。</para>
    /// Gets the display text for the level (abbreviation + name).
    /// </summary>
    public string LevelText => $"{LogViewModel.LevelShort(Level)}-{Level}";

    /// <summary>
    /// <para>获取级别对应的前景色。</para>
    /// Gets the foreground brush for the level.
    /// </summary>
    public IBrush LevelBrush => Level switch
    {
        LogLevel.Trace => TraceBrush,
        LogLevel.Debug => DebugBrush,
        LogLevel.Warn => WarnBrush,
        LogLevel.Error => ErrorBrush,
        LogLevel.Fatal => FatalBrush,
        LogLevel.Info => InfoBrush,
        _ => throw new NotImplementedException(),
    };
}