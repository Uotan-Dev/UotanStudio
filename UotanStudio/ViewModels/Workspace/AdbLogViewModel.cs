using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.Workspace;

internal partial class AdbLogViewModel : Tool, IDisposable
{
    private const int MaxLines = 5000;
    private readonly Lock _sync = new();
    private readonly Queue<string> _pending = new();
    private readonly Func<DetectedDevice, Action<string>, CancellationToken, Task> _stream;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DeviceViewModel? _devices;
    private DeviceInfo? _selected;
    private CancellationTokenSource? _capture;
    private Task? _captureTask;
    private string? _deviceKey;
    private bool _disposed;

    public ObservableCollection<string> Lines { get; } = [];
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _connectedSerial;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isAvailable;

    public AdbLogViewModel() : this(DeviceMonitorService.Instance.StartLogcatStreamingAsync) { }

    internal AdbLogViewModel(Func<DetectedDevice, Action<string>, CancellationToken, Task> stream)
    {
        _stream = stream;
        _timer.Tick += OnTick;
    }

    // Initialize after panel registration so an existing selection can show the dock.
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
        device is { Protocol: DeviceProtocolKind.Adb } &&
        device.State.ToLowerInvariant() is not ("offline" or "unauthorized" or "sideload" or "disconnected");

    internal void SetDevice(DetectedDevice? device)
    {
        if (_disposed) return;
        var available = SupportsLogcat(device);
        IsAvailable = available;
        WorkspacePanelService.Instance.SetAdbLogAvailable(available);
        if (available && device!.Key == _deviceKey && _captureTask is { IsCompleted: false }) return;

        var previous = _captureTask;
        StopCapture();
        Clear();
        ErrorMessage = null;
        if (!available) return;

        _deviceKey = device!.Key;
        ConnectedSerial = device.Serial;
        IsRunning = true;
        var capture = new CancellationTokenSource();
        lock (_sync) _capture = capture;
        _captureTask = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                if (previous is not null) await previous.ConfigureAwait(false);
                capture.Token.ThrowIfCancellationRequested();
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
                    IsRunning = false;
                    ErrorMessage = error;
                });
                capture.Dispose();
            }
        });
    }

    private void Enqueue(CancellationTokenSource capture, string line)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_capture, capture)) return;
            if (_pending.Count == MaxLines) _pending.Dequeue();
            _pending.Enqueue(line.Length > AdbLogcatLineDecoder.MaximumLineLength
                ? line[..AdbLogcatLineDecoder.MaximumLineLength] : line);
        }
    }

    private void OnTick(object? sender, EventArgs e) => FlushPending();

    internal void FlushPending()
    {
        string[] batch;
        lock (_sync)
        {
            var count = Math.Min(250, _pending.Count);
            batch = new string[count];
            for (var i = 0; i < count; i++) batch[i] = _pending.Dequeue();
        }
        foreach (var line in batch)
        {
            if (Lines.Count == MaxLines) Lines.RemoveAt(0);
            Lines.Add(line);
        }
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
    }

    public void Clear()
    {
        lock (_sync) _pending.Clear();
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
        GC.SuppressFinalize(this);
    }
}
