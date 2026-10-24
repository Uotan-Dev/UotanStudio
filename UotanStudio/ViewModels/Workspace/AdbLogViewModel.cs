using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Logging;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.Workspace;

internal partial class AdbLogViewModel : Tool, IDisposable, ILogViewModel
{
    /// <summary>Upper bound of entries published by one timer tick; each flush raises one range notification.
    /// <para>单个计时器 tick 发布的条目上限；每次刷新只触发一次区间通知。</para>
    /// </summary>
    private const int FlushBatchEntries = 250;

    /// <summary>Upper bound of source characters published by one timer tick, so one flush never inserts an
    /// unbounded amount of text into the log view.
    /// <para>单个计时器 tick 发布的原始字符上限，使一次刷新插入日志视图的文本量有界。</para>
    /// </summary>
    private const int FlushBatchCharacters = 64 * 1024;

    /// <summary>Consecutive capture failures after which automatic recovery stops and keeps the last error visible.
    /// <para>连续抓取失败达到该次数后停止自动恢复，并保留最后一次错误提示。</para>
    /// </summary>
    private const int CaptureFailureLimit = 5;

    /// <summary>First wait before a failed or empty capture is retried automatically; it doubles up to the limit below.
    /// <para>抓取失败或空结束后的首次自动重试等待；之后逐次翻倍直到下方上限。</para>
    /// </summary>
    private static readonly TimeSpan CaptureRestartBackoff = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound of the automatic retry wait.
    /// <para>自动重试等待的上限。</para>
    /// </summary>
    private static readonly TimeSpan CaptureRestartBackoffLimit = TimeSpan.FromSeconds(60);

    private readonly Lock _sync = new();
    private readonly BoundedDeviceLogBuffer _pending = new();
    private readonly BoundedDeviceLogBuffer _allLines = new();
    private readonly Func<DetectedDevice, Action<AdbLogcatEntry>, CancellationToken, Task> _stream;
    private readonly Func<DetectedDevice, CancellationToken, Task> _clearDeviceBuffers;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<AdbLogcatEntry> _batch = new(FlushBatchEntries);
    private readonly List<AdbLogcatEntry> _appended = new(FlushBatchEntries);
    private Action<AdbLogcatEntry>? _onEvicted;
    private int _removedCount;
    private DeviceViewModel? _devices;
    private DeviceInfo? _selected;
    private CancellationTokenSource? _capture;
    private Task? _captureTask;
    private string? _deviceKey;
    private string? _deviceState;
    private DetectedDevice? _activeDevice;
    private Regex? _searchPattern;
    private int _captureFailureCount;
    private long _nextCaptureAttemptTick;
    private bool _captureRecoveryStopped;
    private bool _disposed;

    public LogEntryCollection<AdbLogcatEntry> Lines { get; } = [];
    AdbLogViewModel? ILogViewModel.LiveModel => this;
    Editors.LogEditorViewModel? ILogViewModel.FileModel => null;
    IReadOnlyList<ILogLine> ILogViewModel.Entries => Lines;
    event NotifyCollectionChangedEventHandler? ILogViewModel.EntriesChanged
    {
        add => Lines.CollectionChanged += value;
        remove => Lines.CollectionChanged -= value;
    }
    System.Windows.Input.ICommand ILogViewModel.ClearSearchCommand => ClearSearchCommand;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearLogsCommand))]
    private bool _isClearing;
    [ObservableProperty] private string? _connectedSerial;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLevelIndex))]
    private LogLevel? _levelFilter;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchError))]
    private string? _searchError;

    public bool HasSearchError => SearchError is not null;

    public IReadOnlyList<LogLevel?> Levels { get; } = [null, LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal];

    public int SelectedLevelIndex
    {
        get => LevelFilter.HasValue ? (int)LevelFilter.Value + 1 : 0;
        set
        {
            if (value >= 0 && value < Levels.Count) LevelFilter = Levels[value];
        }
    }

    partial void OnLevelFilterChanged(LogLevel? value) => ReapplyFilter();
    partial void OnSearchTextChanged(string value) => UpdateSearchFilter();
    partial void OnUseRegexChanged(bool value) => UpdateSearchFilter();
    partial void OnMatchCaseChanged(bool value) => UpdateSearchFilter();
    partial void OnIsPausedChanged(bool value)
    {
        if (!value) FlushPending();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ResetLogs() => Clear();

    private bool CanClearLogs() => !_disposed && !IsClearing;

    /// <summary>
    /// Clears the local view and the device log buffers. The device buffers are cleared through the device's
    /// existing ADB route on its own shell stream while the capture keeps streaming, so clearing never stops or
    /// reopens the log stream.
    /// <para>清空本地视图与设备日志缓冲区。设备缓冲区经该设备既有的 ADB 路由在独立 shell 流上清空，抓取保持流式运行，
    /// 因此清空不会停止或重开日志流。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearLogs))]
    private async Task ClearLogsAsync()
    {
        if (_disposed)
        {
            return;
        }

        var device = _activeDevice;
        Clear();
        ErrorMessage = null;
        ResetCaptureRecovery();
        if (!SupportsLogcat(device))
        {
            return;
        }

        IsClearing = true;
        try
        {
            await Task.Run(() => _clearDeviceBuffers(device!, CancellationToken.None)).ConfigureAwait(true);
            if (!_disposed)
            {
                ErrorMessage = null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed)
            {
                ErrorMessage = string.Format(
                    LanguageService.Instance.GetString("AdbLog_ClearFailed"), UserError.Describe(ex));
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsClearing = false;
            }
        }
    }

    private void UpdateSearchFilter()
    {
        _searchPattern = null;
        SearchError = null;
        if (UseRegex && !string.IsNullOrEmpty(SearchText))
        {
            try
            {
                var options = RegexOptions.CultureInvariant;
                if (!MatchCase) options |= RegexOptions.IgnoreCase;
                _searchPattern = new Regex(SearchText, options, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException ex)
            {
                SearchError = string.Format(LanguageService.Instance.GetString("Log_RegexInvalid"), UserError.Describe(ex));
            }
        }
        ReapplyFilter();
    }

    private bool MatchesFilter(AdbLogcatEntry line)
    {
        if (HasSearchError || (LevelFilter.HasValue && line.Level != LevelFilter)) return false;
        if (string.IsNullOrEmpty(SearchText)) return true;
        if (!UseRegex)
            return line.Text.Contains(SearchText, MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        try
        {
            return _searchPattern?.IsMatch(line.Text) == true;
        }
        catch (RegexMatchTimeoutException)
        {

            SearchError = LanguageService.Instance.GetString("Log_RegexTimeout");
            return false;
        }
    }

    private void ReapplyFilter()
    {
        List<AdbLogcatEntry> filtered = [];
        foreach (var line in _allLines.Entries)
        {
            if (MatchesFilter(line)) filtered.Add(line);
            if (HasSearchError) break;
        }
        Lines.Replace(HasSearchError ? [] : filtered);
    }

    public AdbLogViewModel() : this(App.DeviceMonitor.StartLogcatStreamingAsync,
        App.DeviceMonitor.ClearLogcatBuffersAsync)
    { }

    internal AdbLogViewModel(Func<DetectedDevice, Action<AdbLogcatEntry>, CancellationToken, Task> stream,
        Func<DetectedDevice, CancellationToken, Task> clearDeviceBuffers)
    {
        _stream = stream;
        _clearDeviceBuffers = clearDeviceBuffers;
        _timer.Tick += OnTick;
    }

    internal void Initialize()
    {
        if (_devices is not null || _disposed) return;
        _devices = DeviceViewModel.EnsureInstance();
        _devices.PropertyChanged += OnDevicePropertyChanged;
        _timer.Start();
        UpdateSelection();
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.SelectedDevice) or nameof(DeviceInfo.State))
            UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_disposed) return;
        if (_selected is not null) _selected.PropertyChanged -= OnDevicePropertyChanged;
        _selected = _devices?.SelectedDevice;
        if (_selected is not null) _selected.PropertyChanged += OnDevicePropertyChanged;
        SetDevice(_selected?.Device);
    }

    internal static bool SupportsLogcat(DetectedDevice? device) =>
        device is { Protocol: DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc } &&
        device.State.ToLowerInvariant() is not ("offline" or "unauthorized" or "sideload" or "disconnected");

    internal void SetDevice(DetectedDevice? device)
    {
        if (_disposed) return;
        var changed = device?.Key != _deviceKey || device?.State != _deviceState;
        var sameDevice = _deviceKey is not null && device?.Key == _deviceKey;
        _activeDevice = device;
        var available = SupportsLogcat(device);
        IsAvailable = available;
        if (!available)
        {
            ResetCaptureRecovery();
            RestartCapture(null, clearLines: true);
            return;
        }

        if (changed)
        {
            ResetCaptureRecovery();
        }
        else if (_captureTask is { IsCompleted: false } || _captureRecoveryStopped ||
            Environment.TickCount64 < _nextCaptureAttemptTick)
        {
            // The capture is running, automatic recovery already gave up, or the backoff window is still
            // open; the timer tick owns the next attempt instead of this selection notification.
            return;
        }

        // A different device starts with an empty view; a state change on the same device keeps the lines the
        // user is already reading.
        RestartCapture(device, clearLines: !sameDevice);
    }

    private void RestartCapture(DetectedDevice? device, bool clearLines)
    {
        if (_disposed) return;
        var previous = _captureTask;
        StopCapture();
        if (clearLines)
        {
            Clear();
            ErrorMessage = null;
        }
        if (!SupportsLogcat(device)) return;

        _deviceKey = device!.Key;
        _deviceState = device.State;
        ConnectedSerial = device.Serial;
        IsRunning = true;
        // A running capture owns the retry window; the value also keeps a capture that ends without posting its
        // completion from turning the timer into a hot restart loop.
        _nextCaptureAttemptTick = Environment.TickCount64 + (long)CaptureRestartBackoff.TotalMilliseconds;
        var capture = new CancellationTokenSource();
        lock (_sync) _capture = capture;
        var received = 0;
        _captureTask = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                if (previous is not null) await previous.ConfigureAwait(false);
                capture.Token.ThrowIfCancellationRequested();
                await _stream(device, line =>
                {
                    if (Interlocked.Exchange(ref received, 1) == 0)
                    {
                        // The first line of a (re)started capture proves the stream works, so the previous
                        // failure text and its backoff no longer describe the current state.
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_disposed || !ReferenceEquals(_capture, capture)) return;
                            ResetCaptureRecovery();
                            ErrorMessage = null;
                        });
                    }
                    Enqueue(capture, line);
                }, capture.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (capture.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!capture.IsCancellationRequested)
                {
                    error = UserError.Describe(ex);
                }
            }
            finally
            {
                var produced = Volatile.Read(ref received) != 0;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || !ReferenceEquals(_capture, capture)) return;
                    lock (_sync)
                    {
                        if (ReferenceEquals(_capture, capture)) _capture = null;
                    }
                    IsRunning = false;
                    ErrorMessage = error;
                    ScheduleCaptureRecovery(error is not null || !produced);
                });
                capture.Dispose();
            }
        });
    }

    /// <summary>
    /// Cancels the current capture and arms the automatic retry, so a capture that failed or ended without
    /// output restarts by itself once the backoff window is over instead of waiting for a device switch.
    /// <para>取消当前抓取并安排自动重试，使失败或空结束的抓取在退避窗口结束后自行重试，而不是等切换设备。</para>
    /// </summary>
    private void ScheduleCaptureRecovery(bool unhealthy)
    {
        if (!unhealthy)
        {
            _captureFailureCount = 0;
            _captureRecoveryStopped = false;
            _nextCaptureAttemptTick = Environment.TickCount64 + (long)CaptureRestartBackoff.TotalMilliseconds;
            return;
        }

        _captureFailureCount++;
        if (_captureFailureCount >= CaptureFailureLimit)
        {
            _captureRecoveryStopped = true;
            _nextCaptureAttemptTick = long.MaxValue;
            ErrorMessage ??= UserError.Describe(new IOException(
                $"The logcat stream for {_deviceKey} produced no usable data {_captureFailureCount} times in a row; "
                + "automatic restart stopped."));
            LogService.Instance.Write(LogLevel.Warn, "AdbLog",
                $"Logcat capture for {_deviceKey} stopped after {_captureFailureCount} consecutive failures.");
            return;
        }

        var backoff = CaptureBackoff(_captureFailureCount);
        _nextCaptureAttemptTick = Environment.TickCount64 + (long)backoff.TotalMilliseconds;
    }

    private static TimeSpan CaptureBackoff(int failures)
    {
        var factor = Math.Pow(2, Math.Clamp(failures - 1, 0, 8));
        var milliseconds = CaptureRestartBackoff.TotalMilliseconds * factor;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, CaptureRestartBackoffLimit.TotalMilliseconds));
    }

    private void ResetCaptureRecovery()
    {
        _captureFailureCount = 0;
        _captureRecoveryStopped = false;
        _nextCaptureAttemptTick = 0;
    }

    private void Enqueue(CancellationTokenSource capture, AdbLogcatEntry entry)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_capture, capture)) return;
            _pending.Add(entry);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        FlushPending();
        TryRestartCapture();
    }

    /// <summary>
    /// Restarts the capture for the selected device once its retry window has elapsed, so log reception
    /// recovers without a manual device switch and without reopening the transport in a tight loop.
    /// <para>退避窗口结束后为当前设备重试抓取，使日志接收无需手动切换设备即可恢复，且不会在紧密循环中反复重开传输。</para>
    /// </summary>
    private void TryRestartCapture()
    {
        if (_disposed || _captureTask is { IsCompleted: false })
        {
            return;
        }

        var device = _activeDevice;
        if (!SupportsLogcat(device) || _captureRecoveryStopped)
        {
            return;
        }

        if (Environment.TickCount64 < _nextCaptureAttemptTick)
        {
            return;
        }

        RestartCapture(device, clearLines: false);
    }

    internal void FlushPending()
    {
        if (IsPaused) return;
        var batch = _batch;
        batch.Clear();
        lock (_sync)
        {
            var characters = 0;
            while (_pending.Count > 0 && batch.Count < FlushBatchEntries && characters < FlushBatchCharacters)
            {
                var entry = _pending.Take();
                batch.Add(entry);
                characters += entry.RawText.Length;
            }
        }
        if (batch.Count == 0) return;
        _removedCount = 0;
        var evicted = _onEvicted ??= OnEvicted;
        var appended = _appended;
        appended.Clear();
        foreach (var entry in batch)
        {
            _allLines.Add(entry, evicted);
            if (MatchesFilter(entry)) appended.Add(entry);
        }
        if (HasSearchError) Lines.Replace([]);
        else Lines.Apply(_removedCount, appended);
    }

    private void OnEvicted(AdbLogcatEntry removed)
    {
        if (_removedCount < Lines.Count && ReferenceEquals(Lines[_removedCount], removed))
        {
            _removedCount++;
        }
    }

    private void StopCapture()
    {
        lock (_sync)
        {
            var capture = _capture;
            _capture = null;

            try { capture?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _deviceKey = null;
        _deviceState = null;
        ConnectedSerial = null;
        IsRunning = false;
        IsClearing = false;
    }

    public void Clear()
    {
        lock (_sync) _pending.Clear();
        _allLines.Clear();
        Lines.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        if (_devices is not null) _devices.PropertyChanged -= OnDevicePropertyChanged;
        if (_selected is not null) _selected.PropertyChanged -= OnDevicePropertyChanged;
        StopCapture();
        Clear();
        ClearLogsCommand.NotifyCanExecuteChanged();
        GC.SuppressFinalize(this);
    }
}