using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Watches the system serial-port list and handshakes a MediaTek preloader the moment its COM port appears.
/// A preloader that enumerates as a USB-to-serial adapter accepts the complemented boot handshake for only a
/// few seconds, and a full USB inventory walk takes longer than that; <see cref="SerialTransport.GetAvailablePorts"/>
/// answers in well under a millisecond, so this is the only watcher that can meet the window.
/// <para>监视系统串口列表，并在联发科 preloader 的 COM 口出现的瞬间握手。枚举为 USB 转串口适配器的 preloader
/// 只在数秒内接受反码 boot 握手，而整棵 USB 清单遍历比这更慢；<see cref="SerialTransport.GetAvailablePorts"/>
/// 在毫秒以内答复，因此这是唯一能赶上该窗口的监视方式。</para>
/// </summary>
internal sealed class MtkComPortProbe : IDisposable
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

    internal MtkComPortProbe(Action<string> onPortArrived, Action<Exception>? onError = null)
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