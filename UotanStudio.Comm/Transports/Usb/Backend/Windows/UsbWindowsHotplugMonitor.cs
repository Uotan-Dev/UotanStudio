using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Event-driven USB device monitoring on Windows via a hidden Message-only window that
/// registers <c>RegisterDeviceNotification</c> for the known interface GUIDs and re-runs
/// enumeration + diff on every <c>WM_DEVICECHANGE</c> (arrival/removal) event.
/// <para>Windows 上的事件驱动 USB 设备监视：通过隐藏消息窗口为已知接口 GUID 注册
/// <c>RegisterDeviceNotification</c>，每次收到 <c>WM_DEVICECHANGE</c>（插入/移除）事件时
/// 重新枚举并做快照对比。</para>
/// Falls back to <see cref="UsbDeviceMonitor"/> polling at the caller when the hidden window
/// cannot be created (e.g. headless/restricted sessions).
/// <para>无法创建隐藏窗口（例如受限会话）时，由调用方回退到 <see cref="UsbDeviceMonitor"/> 轮询。</para>
/// The constructor waits a bounded time for the window and the device-notification registrations
/// to complete and throws when that startup fails, so the failure surfaces synchronously instead
/// of only through the error callback.
/// <para>构造函数在有界时间内等待窗口和设备通知注册完成；启动失败时抛出异常，使失败同步暴露，
/// 而不只是经错误回调报告。</para>
/// </summary>
internal sealed class UsbWindowsHotplugMonitor : IDisposable
{
    private readonly Func<IReadOnlyList<UsbDeviceInfo>> _enumerator;
    private readonly Action<IReadOnlyList<UsbDeviceChange>> _onChanged;
    private readonly Action<Exception>? _onError;
    private readonly Lock _gate = new();
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private Exception? _startupError;
    private IntPtr _hwnd;
    private Dictionary<string, UsbDeviceInfo> _lastSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private int _polling, _pending;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(2);

    private static readonly Win32API.WndProcDelegate WindowProcedure = DispatchWindowMessage;

    private static readonly ConcurrentDictionary<IntPtr, UsbWindowsHotplugMonitor> Windows = new();

    private static readonly Guid[] InterfaceGuids =
    [
        new Guid("a5dcbf10-6530-11d2-901f-00c04fb951ed"),
        new Guid("f72fe0d4-cbcb-407d-8814-9ed673d0dd6b"),
        new Guid("77395066-6C05-4B91-8071-3D7E2409546E"),
        new Guid("4D36E978-E325-11CE-BFC1-08002BE10318")
    ];

    public UsbWindowsHotplugMonitor(
        Func<IReadOnlyList<UsbDeviceInfo>> enumerator,
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        Action<Exception>? onError)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onError = onError;

        _lastSnapshot = UsbDeviceMonitor.BuildMap(_enumerator());

        _thread = new Thread(MessagePump)
        {
            IsBackground = true,
            Name = "UotanStudio.Comm Windows USB hotplug"
        };
        _thread.Start();

        if (!_ready.Wait(StartupTimeout))
        {
            Dispose();
            throw new TimeoutException(
                $"The Windows USB hotplug message window did not become ready within {StartupTimeout.TotalSeconds:0.#} s.");
        }

        if (Volatile.Read(ref _startupError) is { } startupError)
        {
            Dispose();
            throw new InvalidOperationException(
                "The Windows USB hotplug message window could not be created; falling back to polling.",
                startupError);
        }
    }

    private void MessagePump()
    {
        var className = "FirmwareKitCommHotplugWindow";
        var classNamePtr = Marshal.StringToHGlobalUni(className);
        var wc = new Win32API.WNDCLASSW
        {
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            LpszClassName = classNamePtr,
            HInstance = Win32API.GetModuleHandleW(null)
        };

        var readySignalled = false;
        try
        {
            if (Win32API.RegisterClassW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException($"RegisterClassW failed: {Marshal.GetLastWin32Error()}");
            }

            var hwnd = Win32API.CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.HInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
            }

            Windows[hwnd] = this;

            bool disposed;
            lock (_gate)
            {
                _hwnd = hwnd;
                disposed = _disposed;
            }

            if (disposed)
            {
                _ = Win32API.PostMessageW(hwnd, Win32API.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            foreach (var guid in InterfaceGuids)
            {
                var filter = new Win32API.DEV_BROADCAST_DEVICEINTERFACE_W
                {
                    Dbcc_size = (uint)Marshal.OffsetOf<Win32API.DEV_BROADCAST_DEVICEINTERFACE_W>("Dbcc_name"),
                    Dbcc_devicetype = Win32API.DBT_DEVTYP_DEVICEINTERFACE,
                    Dbcc_classguid = guid
                };
                var filterPtr = Marshal.AllocHGlobal(Marshal.SizeOf(filter));
                try
                {
                    Marshal.StructureToPtr(filter, filterPtr, false);
                    if (Win32API.RegisterDeviceNotificationW(hwnd, filterPtr, Win32API.DEVICE_NOTIFY_WINDOW_HANDLE) == IntPtr.Zero)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"RegisterDeviceNotificationW failed for {guid}.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(filterPtr);
                }
            }

            _ready.Set();
            readySignalled = true;

            while (Win32API.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
            {
                _ = Win32API.TranslateMessage(ref msg);
                _ = Win32API.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            if (readySignalled)
            {
                _onError?.Invoke(ex);
            }
            else
            {
                Volatile.Write(ref _startupError, ex);
            }

            UsbTrace.Log($"UsbWindowsHotplugMonitor Message pump failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                _ready.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            Marshal.FreeHGlobal(classNamePtr);
            IntPtr hwnd;
            lock (_gate)
            {
                hwnd = _hwnd;
                _hwnd = IntPtr.Zero;
            }

            _ = Windows.TryRemove(hwnd, out _);

            if (hwnd != IntPtr.Zero)
            {
                _ = Win32API.DestroyWindow(hwnd);
            }
        }
    }

    private static IntPtr DispatchWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32API.WM_DEVICECHANGE &&
            (wParam == (IntPtr)Win32API.DBT_DEVICEARRIVAL || wParam == (IntPtr)Win32API.DBT_DEVICEREMOVECOMPLETE) &&
            Windows.TryGetValue(hWnd, out var monitor))
        {
            monitor.TriggerPoll();
        }

        return Win32API.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void TriggerPoll()
    {
        Interlocked.Exchange(ref _pending, 1);
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(PollLoop);
    }

    private void PollLoop()
    {
        try
        {
            do
            {
                try
                {
                    PollOnce();
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                    UsbTrace.Log($"UsbWindowsHotplugMonitor poll failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            while (Interlocked.Exchange(ref _pending, 0) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
            if (Volatile.Read(ref _pending) != 0)
            {
                TriggerPoll();
            }
        }
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
                UsbTrace.Log($"UsbWindowsHotplugMonitor change callback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        IntPtr hwnd;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hwnd = _hwnd;
        }

        if (hwnd != IntPtr.Zero)
        {
            _ = Win32API.PostMessageW(hwnd, Win32API.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            _ = thread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
        _lastSnapshot.Clear();
    }
}