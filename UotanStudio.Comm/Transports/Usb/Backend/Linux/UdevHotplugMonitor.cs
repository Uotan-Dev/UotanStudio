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

    private readonly Func<IReadOnlyList<UsbDeviceInfo>> _enumerator;
    private readonly Action<IReadOnlyList<UsbDeviceChange>> _onChanged;
    private readonly Action<Exception>? _onError;
    private readonly Lock _gate = new();
    private readonly int _socket;
    private Dictionary<string, UsbDeviceInfo> _lastSnapshot;
    private CancellationTokenSource? _receiveLoop;
    private bool _disposed;

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
        _ = Task.Run(() => ReceiveLoop(_receiveLoop.Token));
    }

    private void ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var lastEventTicks = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            var received = ReceiveFrom(buffer);

            if (received <= 0)
            {
                if (Marshal.GetLastWin32Error() == 4 /* EINTR */)
                {
                    continue;
                }

                break;
            }

            if (!IsUsbDeviceUevent(buffer, received))
            {
                continue;
            }

            // Coalesce bursts of uevents (one plug typically emits several) into one rescan.
            var now = Environment.TickCount64;
            if (now - lastEventTicks < 100)
            {
                continue;
            }

            lastEventTicks = now;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    PollOnce();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                    UsbTrace.Log($"UdevHotplugMonitor poll failed: {ex.GetType().Name}: {ex.Message}");
                }
            }, cancellationToken);
        }
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

    private static bool IsUsbDeviceUevent(byte[] buffer, int length)
    {
        // Kernel uevents are NUL-separated; the first item is "action@DEVPATH" and the
        // environment follows. Only whole usb_device events trigger a rescan.
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
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _receiveLoop?.Cancel();
        _receiveLoop?.Dispose();
        _receiveLoop = null;
        UdevLibcInterop.close(_socket);
        _lastSnapshot.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SockaddrNetlink
    {
        public ushort Family;
        public ushort Padding;
        public uint Groups;
        public uint Pid;

        public const int Size = 12; // ushort + ushort + uint + uint, packed

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