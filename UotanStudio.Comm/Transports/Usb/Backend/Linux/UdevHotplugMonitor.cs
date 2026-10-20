using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

/// <summary>
/// Monitors USB device arrivals/removals through the kernel uevent netlink socket
/// (AF_NETLINK / NETLINK_KOBJECT_UEVENT, udev's event source), so the native Linux backend
/// gets event-driven hotplug without libudev. Falls back to polling when the socket cannot
/// be created (e.g. restricted sandbox).
/// <para>通过内核 uevent netlink 套接字（AF_NETLINK / NETLINK_KOBJECT_UEVENT，即 udev 的事件源）
/// 监视 USB 设备新增/移除，使 Linux 原生后端无需 libudev 即可获得事件驱动热插拔。
/// 套接字创建失败（如受限沙箱）时由调用方回退到轮询。</para>
/// </summary>
internal sealed class UdevHotplugMonitor : IDisposable
{
    private const int AfNetlink = 16;
    private const int NetlinkKobjectUevent = 15;
    private const int SockDGram = 2;
    private const int SockCloexec = 0x80000;
    private static readonly TimeSpan ReceiveShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<IReadOnlyList<UsbDeviceInfo>> _enumerator;
    private readonly Action<IReadOnlyList<UsbDeviceChange>> _onChanged;
    private readonly Action<Exception>? _onError;
    private readonly Lock _gate = new();
    private readonly int _socket;
    private Dictionary<string, UsbDeviceInfo> _lastSnapshot;
    private CancellationTokenSource? _receiveLoop;
    private Task? _receiveTask;
    private volatile bool _disposed;
    private CancellationToken _token;
    private int _polling, _pending;

    public UdevHotplugMonitor(
        Func<IReadOnlyList<UsbDeviceInfo>> enumerator,
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        Action<Exception>? onError)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("The uevent netlink monitor is Linux-only.");
        }

        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onError = onError;

        _socket = UdevLibcInterop.socket(AfNetlink, SockDGram | SockCloexec, NetlinkKobjectUevent);
        if (_socket < 0)
        {
            throw new IOException($"Could not create the uevent netlink socket (errno {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var address = SockaddrNetlink.FromGroups(1);
            if (UdevLibcInterop.bind(_socket, ref address, SockaddrNetlink.Size) < 0)
            {
                throw new IOException($"Could not bind the uevent netlink socket (errno {Marshal.GetLastWin32Error()}).");
            }
        }
        catch
        {
            UdevLibcInterop.close(_socket);
            throw;
        }

        _lastSnapshot = UsbDeviceMonitor.BuildMap(_enumerator());
        _receiveLoop = new CancellationTokenSource();
        _token = _receiveLoop.Token;
        _receiveTask = Task.Run(() => ReceiveLoop(_token));
    }

    private void ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = ReceiveFrom(buffer);
                if (_disposed)
                {
                    return;
                }

                if (received <= 0)
                {
                    if (!ShouldRetryAfterReceive(received, Marshal.GetLastWin32Error()))
                    {
                        break;
                    }

                    continue;
                }

                if (!IsUsbDeviceUevent(buffer, received))
                {
                    continue;
                }

                TriggerPoll();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
            UsbTrace.Log($"UdevHotplugMonitor receive loop failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TriggerPoll()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _pending, 1);
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    await Task.Delay(100, _token).ConfigureAwait(false);
                    PollOnce();
                }
                while (Interlocked.Exchange(ref _pending, 0) != 0);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                UsbTrace.Log($"UdevHotplugMonitor poll failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
                if (!_disposed && Volatile.Read(ref _pending) != 0)
                {
                    TriggerPoll();
                }
            }
        }, _token);
    }

    /// <summary>Blocking recvfrom into the managed buffer; isolated so the loop stays await-safe.</summary>
    private unsafe int ReceiveFrom(byte[] buffer)
    {
        fixed (byte* bufferPointer = buffer)
        {
            var source = SockaddrNetlink.None;
            var sourceSize = SockaddrNetlink.Size;
            return UdevLibcInterop.recvfrom(_socket, bufferPointer, buffer.Length, 0, ref source, ref sourceSize);
        }
    }

    /// <summary>
    /// Decides whether a non-positive recvfrom result keeps the uevent receive loop alive. A
    /// return of <c>0</c> is a legitimate interrupted/no-data datagram receive on the netlink
    /// socket and must not be reported as a socket failure, so it continues regardless of the
    /// (possibly stale) errno value. A negative return with EINTR is the standard syscall-restart
    /// case. Every other non-positive result is a real failure and ends the loop so the caller
    /// can fall back to polling.
    /// <para>判定非正的 recvfrom 结果是否让 uevent 接收循环继续存活：netlink 套接字上返回 <c>0</c>
    /// 是合法的中断/无数据数据报接收，不得上报为套接字故障，因此无论 errno（可能已过期）为何值都继续。
    /// 返回负值且 errno 为 EINTR 是标准的系统调用重启场景。其余非正结果都是真实故障，结束循环让
    /// 调用方回退到轮询。</para>
    /// </summary>
    /// <param name="received">The recvfrom return value; only non-positive values are consulted here. <para>recvfrom 的返回值；此处仅判定非正值。</para></param>
    /// <param name="lastError">The errno captured after the receive call. <para>接收调用后捕获的 errno。</para></param>
    /// <returns><c>true</c> when the loop should wait for the next datagram instead of exiting. <para>循环应继续等待下一个数据报而非退出时为 <c>true</c>。</para></returns>
    internal static bool ShouldRetryAfterReceive(int received, int lastError)
        => received == 0 || received < 0 && lastError == LinuxUsbAPI.EINTR;

    private static bool IsUsbDeviceUevent(byte[] buffer, int length)
    {
        var span = new ReadOnlySpan<byte>(buffer, 0, length);
        var text = Encoding.UTF8.GetString(span);
        if (!text.StartsWith("add@", StringComparison.Ordinal) &&
            !text.StartsWith("remove@", StringComparison.Ordinal) &&
            !text.StartsWith("change@", StringComparison.Ordinal))
        {
            return false;
        }

        return text.Contains("SUBSYSTEM=usb", StringComparison.Ordinal) &&
            text.Contains("DEVTYPE=usb_device", StringComparison.Ordinal);
    }

    private void PollOnce()
    {
        if (_disposed)
        {
            return;
        }

        var currentSnapshot = UsbDeviceMonitor.BuildMap(_enumerator());
        List<UsbDeviceChange>? changes;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            changes = UsbDeviceMonitor.ComputeChanges(currentSnapshot, _lastSnapshot);
            _lastSnapshot = currentSnapshot;
        }

        if (changes is { Count: > 0 })
        {
            try
            {
                _onChanged(changes);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                UsbTrace.Log($"UdevHotplugMonitor change callback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Task? receiveTask;
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            receiveTask = _receiveTask;
            _receiveTask = null;
            lifetime = _receiveLoop;
            _receiveLoop = null;
        }

        lifetime?.Cancel();
        UdevLibcInterop.close(_socket);
        if (receiveTask is not null)
        {
            try
            {
                receiveTask.WaitAsync(ReceiveShutdownTimeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                UsbTrace.Log("UdevHotplugMonitor receive loop did not stop within the shutdown timeout.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                UsbTrace.Log($"UdevHotplugMonitor receive loop faulted during shutdown: {ex.GetType().Name}: {ex.Message}");
            }
        }

        lifetime?.Dispose();
        lock (_gate)
        {
            _lastSnapshot.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SockaddrNetlink
    {
        public ushort Family;
        public ushort Padding;
        public uint Groups;
        public uint Pid;

        public const int Size = 12;

        public static SockaddrNetlink None => default;

        public static SockaddrNetlink FromGroups(uint groups) => new()
        {
            Family = AfNetlink,
            Groups = groups
        };
    }
}

/// <summary>
/// libc bindings for the uevent netlink socket; only linked at runtime on Linux where this
/// monitor is constructed.
/// <para>uevent netlink 套接字的 libc 绑定；仅在本监视器构建的 Linux 平台运行时链接。</para>
/// </summary>
internal static unsafe partial class UdevLibcInterop
{
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int bind(int socket, ref UdevHotplugMonitor.SockaddrNetlink address, int addressLength);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int recvfrom(
        int socket, byte* buffer, int length, int flags, ref UdevHotplugMonitor.SockaddrNetlink source, ref int sourceLength);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int socket);
}