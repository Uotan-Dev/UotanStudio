using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Watches the system serial-port list and reports the moment a boot-protocol COM port appears. The actual
/// handshake is dispatched by the arrival pipeline using the protocol classified from the PnP hardware id.
/// <para>监视系统串口列表，并在启动协议 COM 口出现的瞬间上报。实际握手由到达流水线根据 PnP 硬件 ID
/// 分类出的协议分派。</para>
/// </summary>
internal sealed class FastComPortProbe : IDisposable
{
    /// <summary>Scan cadence. Fast enough to catch the window, cheap enough to run continuously.</summary>
    /// <para>扫描节奏。快到足以赶上窗口，又便宜到可以持续运行。</para>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(100);

    private readonly Action<string> _onPortArrived;
    private readonly Action<Exception>? _onError;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _knownPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private Task? _loop;
    private bool _disposed;

    internal FastComPortProbe(Action<string> onPortArrived, Action<Exception>? onError = null)
    {
        _onPortArrived = onPortArrived ?? throw new ArgumentNullException(nameof(onPortArrived));
        _onError = onError;
    }

    /// <summary>Starts scanning, seeding the baseline so ports present before start are not reported.
    /// <para>开始扫描，并先建立基线，使启动前已存在的端口不被上报。</para></summary>
    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is not null)
            {
                return;
            }

            foreach (var port in EnumeratePorts())
            {
                _knownPorts.Add(port);
            }

            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }
    }

    private static IReadOnlyList<string> EnumeratePorts()
    {
        try
        {
            return SerialTransport.GetAvailablePorts();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var port in EnumeratePorts())
                {
                    var isNew = false;
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        isNew = _knownPorts.Add(port);
                    }

                    if (!isNew)
                    {
                        continue;
                    }

                    // Only claim ports the read-only PnP tree classifies as a known boot protocol. An unrelated
                    // serial device stays free; a classified one keeps its own protocol when submitted downstream.
                    // 只认领只读 PnP 树判定为已知启动协议的端口。无关串口保持原样；已分类端口在下游保留自己的协议。
                    if (OperatingSystem.IsWindows() &&
                        FastBootPortEnumerator.ClassifyPort(port) is null)
                    {
                        continue;
                    }

                    try
                    {
                        _onPortArrived(port);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _onError?.Invoke(ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Forgets a port so a later reappearance is reported again.
    /// <para>遗忘某个端口，使其再次出现时重新上报。</para></summary>
    internal void Forget(string port)
    {
        lock (_gate)
        {
            _knownPorts.Remove(port);
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
            _loop = null;
        }

        _lifetime.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _lifetime.Dispose();
    }
}