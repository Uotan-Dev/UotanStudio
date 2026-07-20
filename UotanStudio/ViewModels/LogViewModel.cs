using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Services;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>日志视图模型，管理实时日志条目（ADB logcat / fastboot 输出）。</para>
    /// Log view model, managing real-time log entries (ADB logcat / fastboot output).
    /// </summary>
    public partial class LogViewModel : ViewModelBase
    {
        private ObservableCollection<LogEntry> _entries = [];
        private ObservableCollection<LogEntry> _filteredEntries = [];
        private string _filter = string.Empty;
        private string _levelFilter = string.Empty;
        private bool _autoScroll = true;
        private bool _isCapturing;
        private bool _isPaused;
        private string _statusText = string.Empty;

        private CancellationTokenSource? _captureCts;
        private string? _activeSerial;

        /// <summary>
        /// <para>获取或设置日志条目列表。</para>
        /// Gets or sets the list of log entries.
        /// </summary>
        public ObservableCollection<LogEntry> Entries
        {
            get => _entries;
            set
            {
                if (SetProperty(ref _entries, value))
                {
                    RefreshFilter();
                }
            }
        }

        /// <summary>
        /// <para>获取经过滤后的日志条目列表（供 UI 绑定）。</para>
        /// Gets the filtered log entries list (for UI binding).
        /// </summary>
        public ObservableCollection<LogEntry> FilteredEntries
        {
            get => _filteredEntries;
            private set => SetProperty(ref _filteredEntries, value);
        }

        /// <summary>
        /// <para>获取或设置日志过滤文本。变更时自动重新过滤。</para>
        /// Gets or sets the log filter text. Automatically re-filters on change.
        /// </summary>
        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                {
                    RefreshFilter();
                }
            }
        }

        /// <summary>
        /// <para>获取或设置日志级别过滤（如 W/E，空表示不过滤）。</para>
        /// Gets or sets the log level filter (e.g. W/E; empty means no filter).
        /// </summary>
        public string LevelFilter
        {
            get => _levelFilter;
            set
            {
                if (SetProperty(ref _levelFilter, value))
                {
                    RefreshFilter();
                }
            }
        }

        /// <summary>
        /// <para>获取或设置是否自动滚动到最新日志。</para>
        /// Gets or sets whether to auto-scroll to the latest log entry.
        /// </summary>
        public bool AutoScroll
        {
            get => _autoScroll;
            set => SetProperty(ref _autoScroll, value);
        }

        /// <summary>
        /// <para>获取或设置是否正在捕获日志。</para>
        /// Gets or sets whether log capture is currently active.
        /// </summary>
        public bool IsCapturing
        {
            get => _isCapturing;
            set => SetProperty(ref _isCapturing, value);
        }

        /// <summary>
        /// <para>获取或设置是否暂停捕获。</para>
        /// Gets or sets whether capture is paused.
        /// </summary>
        public bool IsPaused
        {
            get => _isPaused;
            set => SetProperty(ref _isPaused, value);
        }

        /// <summary>
        /// <para>获取或设置状态文本。</para>
        /// Gets or sets the status text.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public LogViewModel()
        {
            // 监听原始集合变更以刷新过滤
            _entries.CollectionChanged += (_, _) => RefreshFilter();
        }

        /// <summary>
        /// <para>根据当前过滤文本与级别过滤刷新 FilteredEntries。</para>
        /// Refreshes FilteredEntries based on the current text and level filters.
        /// </summary>
        private void RefreshFilter()
        {
            var keyword = _filter?.Trim() ?? string.Empty;
            var level = _levelFilter?.Trim() ?? string.Empty;
            var filtered = _entries.Where(e =>
                (string.IsNullOrEmpty(keyword)
                    || (e.Message?.Contains(keyword, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.Source?.Contains(keyword, System.StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.Level?.Contains(keyword, System.StringComparison.OrdinalIgnoreCase) ?? false))
                && (string.IsNullOrEmpty(level)
                    || (e.Level?.Contains(level, System.StringComparison.OrdinalIgnoreCase) ?? false))).ToList();

            FilteredEntries = new ObservableCollection<LogEntry>(filtered);
        }

        /// <summary>
        /// <para>设置当前激活的设备序列号，供 logcat 拉取使用。</para>
        /// Sets the currently active device serial, used for logcat capture.
        /// </summary>
        public void SetActiveDevice(string? serial)
        {
            _activeSerial = serial;
        }

        /// <summary>
        /// <para>开始捕获日志。</para>
        /// Starts capturing logs.
        /// </summary>
        [RelayCommand]
        private async Task StartCaptureAsync()
        {
            if (IsCapturing) return;
            if (string.IsNullOrEmpty(_activeSerial))
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

            Entries.Add(new LogEntry
            {
                Level = "INFO",
                Message = LanguageService.Instance.GetString("Log_CaptureStarted"),
                Source = "System"
            });
            StatusText = LanguageService.Instance.GetString("Log_CaptureStarted");

            try
            {
                await AdbService.Instance.StartLogcatAsync(_activeSerial, line =>
                {
                    if (IsPaused) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        var entry = ParseLogcatLine(line);
                        Entries.Add(entry);
                    });
                }, ct);
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
        /// <para>解析单行 logcat 输出为 LogEntry。</para>
        /// Parses a single logcat output line into a LogEntry.
        /// </summary>
        private static LogEntry ParseLogcatLine(string line)
        {
            // logcat 格式: "MM-DD HH:MM:SS.UUU PID TID LEVEL TAG: MESSAGE"
            // 简化解析：尝试提取级别与标签
            var entry = new LogEntry { Level = "I", Source = "adb", Message = line };
            if (string.IsNullOrEmpty(line)) return entry;

            // 查找形如 " V/" 或 " E/" 的级别标记
            for (int i = 1; i < line.Length - 1; i++)
            {
                if (line[i] == ' ' && "VDIWEF".IndexOf(line[i + 1]) >= 0
                    && i + 2 < line.Length && line[i + 2] == '/')
                {
                    entry.Level = line[i + 1].ToString();
                    var tagStart = i + 3;
                    var tagEnd = line.IndexOf(':', tagStart);
                    if (tagEnd > tagStart)
                    {
                        entry.Source = line.Substring(tagStart, tagEnd - tagStart).Trim();
                        entry.Message = tagEnd + 1 < line.Length
                            ? line.Substring(tagEnd + 1).TrimStart()
                            : string.Empty;
                    }
                    break;
                }
            }
            return entry;
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
            Entries.Add(new LogEntry
            {
                Level = "INFO",
                Message = LanguageService.Instance.GetString("Log_CaptureStopped"),
                Source = "System"
            });
            StatusText = LanguageService.Instance.GetString("Log_CaptureStopped");
        }

        /// <summary>
        /// <para>暂停捕获（logcat 进程继续，但不追加条目到列表）。</para>
        /// Pauses capture (logcat process continues, but entries are not appended).
        /// </summary>
        [RelayCommand]
        private void PauseCapture()
        {
            if (!IsCapturing) return;
            IsPaused = true;
            StatusText = LanguageService.Instance.GetString("Log_Paused");
        }

        /// <summary>
        /// <para>继续捕获（从暂停状态恢复）。</para>
        /// Resumes capture (from paused state).
        /// </summary>
        [RelayCommand]
        private void ResumeCapture()
        {
            if (!IsCapturing) return;
            IsPaused = false;
            StatusText = LanguageService.Instance.GetString("Log_Resumed");
        }

        /// <summary>
        /// <para>清空所有日志条目。</para>
        /// Clears all log entries.
        /// </summary>
        [RelayCommand]
        private void ClearLogs()
        {
            Entries.Clear();
        }

        /// <summary>
        /// <para>导出当前日志条目到指定文件路径。</para>
        /// Exports current log entries to the specified file path.
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>导出是否成功。</returns>
        public async Task<bool> ExportLogsAsync(string filePath)
        {
            try
            {
                await using var writer = new StreamWriter(filePath, append: false);
                foreach (var entry in _entries)
                {
                    await writer.WriteLineAsync($"[{entry.Level}] [{entry.Source}] {entry.Message}");
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
    }

    /// <summary>
    /// <para>日志条目模型。</para>
    /// Log entry model.
    /// </summary>
    public class LogEntry
    {
        /// <summary>日志级别（INFO/WARN/ERROR 等）。</summary>
        public string Level { get; set; } = "INFO";
        /// <summary>日志来源。</summary>
        public string Source { get; set; } = string.Empty;
        /// <summary>日志消息。</summary>
        public string Message { get; set; } = string.Empty;
    }
}
