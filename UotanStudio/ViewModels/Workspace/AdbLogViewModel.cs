using System.Collections.ObjectModel;
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
    private const int MaxLines = 5000;
    private readonly Lock _sync = new();
    private readonly Queue<AdbLogcatEntry> _pending = new();
    private readonly Queue<AdbLogcatEntry> _allLines = new();
    private readonly Func<DetectedDevice, Action<AdbLogcatEntry>, CancellationToken, Task> _stream;
    private readonly Func<DetectedDevice, CancellationToken, Task> _clearDeviceBuffers;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DeviceViewModel? _devices;
    private DeviceInfo? _selected;
    private CancellationTokenSource? _capture;
    private Task? _captureTask;
    private string? _deviceKey;
    private DetectedDevice? _activeDevice;
    private Regex? _searchPattern;
    private bool _disposed;

    public ObservableCollection<AdbLogcatEntry> Lines { get; } = [];
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

    // An index distinguishes the null-valued All item from no ComboBox selection.
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

    [RelayCommand(CanExecute = nameof(CanClearLogs))]
    private void ClearLogs() => RestartCapture(_activeDevice, clearDeviceBuffers: true);

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
                SearchError = string.Format(LanguageService.Instance.GetString("Log_RegexInvalid"), ex.Message);
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
            // Stop retrying this pattern on every incoming line until the search changes.
            SearchError = LanguageService.Instance.GetString("Log_RegexTimeout");
            return false;
        }
    }

    private void ReapplyFilter()
    {
        Lines.Clear();
        foreach (var line in _allLines)
        {
            if (MatchesFilter(line)) Lines.Add(line);
            if (HasSearchError) break;
        }
        if (HasSearchError) Lines.Clear();
    }

    public AdbLogViewModel() : this(DeviceMonitorService.Instance.StartLogcatStreamingAsync,
        DeviceMonitorService.Instance.ClearLogcatBuffersAsync) { }

    internal AdbLogViewModel(Func<DetectedDevice, Action<AdbLogcatEntry>, CancellationToken, Task> stream,
        Func<DetectedDevice, CancellationToken, Task> clearDeviceBuffers)
    {
        _stream = stream;
        _clearDeviceBuffers = clearDeviceBuffers;
        _timer.Tick += OnTick;
    }

    // Start tracking device selection and flushing logcat after layout creation.
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
        _activeDevice = device;
        var available = SupportsLogcat(device);
        IsAvailable = available;
        if (available && device!.Key == _deviceKey && _captureTask is { IsCompleted: false }) return;
        RestartCapture(device, clearDeviceBuffers: false);
    }

    private void RestartCapture(DetectedDevice? device, bool clearDeviceBuffers)
    {
        if (_disposed) return;
        var previous = _captureTask;
        StopCapture();
        Clear();
        ErrorMessage = null;
        if (!SupportsLogcat(device)) return;

        _deviceKey = device!.Key;
        ConnectedSerial = device.Serial;
        IsClearing = clearDeviceBuffers;
        IsRunning = !clearDeviceBuffers;
        var capture = new CancellationTokenSource();
        lock (_sync) _capture = capture;
        _captureTask = Task.Run(async () =>
        {
            string? error = null;
            var clearing = clearDeviceBuffers;
            try
            {
                if (previous is not null) await previous.ConfigureAwait(false);
                capture.Token.ThrowIfCancellationRequested();
                if (clearDeviceBuffers)
                {
                    await _clearDeviceBuffers(device, capture.Token).ConfigureAwait(false);
                    capture.Token.ThrowIfCancellationRequested();
                    clearing = false;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || !ReferenceEquals(_capture, capture)) return;
                        IsClearing = false;
                        IsRunning = true;
                    });
                }
                await _stream(device, line => Enqueue(capture, line), capture.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (capture.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!capture.IsCancellationRequested) error = ex.Message;
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || !ReferenceEquals(_capture, capture)) return;
                    IsClearing = false;
                    IsRunning = false;
                    ErrorMessage = clearing && error is not null
                        ? string.Format(LanguageService.Instance.GetString("AdbLog_ClearFailed"), error)
                        : error;
                });
                capture.Dispose();
            }
        });
    }

    private void Enqueue(CancellationTokenSource capture, AdbLogcatEntry entry)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_capture, capture)) return;
            if (_pending.Count == MaxLines) _pending.Dequeue();
            _pending.Enqueue(entry);
        }
    }

    private void OnTick(object? sender, EventArgs e) => FlushPending();

    internal void FlushPending()
    {
        // Keep the visible history fixed; Enqueue still retains the latest bounded batch.
        if (IsPaused) return;
        AdbLogcatEntry[] batch;
        lock (_sync)
        {
            var count = Math.Min(250, _pending.Count);
            batch = new AdbLogcatEntry[count];
            for (var i = 0; i < count; i++) batch[i] = _pending.Dequeue();
        }
        foreach (var entry in batch)
        {
            if (_allLines.Count == MaxLines)
            {
                var removed = _allLines.Dequeue();
                if (Lines.Count > 0 && ReferenceEquals(Lines[0], removed)) Lines.RemoveAt(0);
            }
            _allLines.Enqueue(entry);
            if (MatchesFilter(entry)) Lines.Add(entry);
        }
        if (HasSearchError && Lines.Count > 0) Lines.Clear();
    }

    private void StopCapture()
    {
        lock (_sync)
        {
            var capture = _capture;
            _capture = null;
            // The worker owns disposal; it may have already completed.
            try { capture?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _deviceKey = null;
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
