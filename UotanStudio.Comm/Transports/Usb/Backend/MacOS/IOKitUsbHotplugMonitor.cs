using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// Event-driven USB hotplug monitoring on macOS through IOKit service notifications.
/// <para>通过 IOKit 服务通知在 macOS 上实现事件驱动 USB 热插拔监视。</para>
/// </summary>
/// <remarks>
/// The monitor registers matched and terminated notifications for both the modern
/// <c>IOUSBHostDevice</c> and classic <c>IOUSBDevice</c> classes. Each callback drains its iterator into
/// per-device hints so the shared incremental snapshot updates only the touched device instead of
/// re-enumerating on every event.
/// <para>监视器为现代 <c>IOUSBHostDevice</c> 与经典 <c>IOUSBDevice</c> 类同时注册匹配与终止通知。
/// 每个回调把迭代器排空为单设备提示，使共享增量快照只更新被触及的设备，而不必对每个事件重新枚举。</para>
/// </remarks>
internal sealed class IOKitUsbHotplugMonitor : IDisposable
{
    private const string MatchedNotification = "IOServiceMatched";
    private const string TerminatedNotification = "IOServiceTerminate";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(2);
    private static readonly IOServiceMatchingCallback MatchedCallback = OnMatched;
    private static readonly IOServiceMatchingCallback TerminatedCallback = OnTerminated;

    /// <summary>Creates a run-loop mode string equal to <c>kCFRunLoopDefaultMode</c> by content.
    /// <para>按内容创建与 <c>kCFRunLoopDefaultMode</c> 相等的运行循环模式字符串。</para></summary>
    /// <remarks><para>
    /// kCFRunLoopDefaultMode is an exported CFString variable, not a stable P/Invoke value; creating an equal
    /// CFString by content avoids dereferencing a dyld-shared-cache data symbol.
    /// kCFRunLoopDefaultMode 是导出的 CFString 变量而非稳定的 P/Invoke 值；按内容创建等值 CFString 可避免
    /// 解引用 dyld 共享缓存中的数据符号。</para></remarks>
    private static readonly IntPtr DefaultRunLoopMode = CreateDefaultRunLoopMode();

    private readonly Func<IReadOnlyList<UsbDeviceProbeHint>, IReadOnlyList<UsbDeviceChange>> _pass;
    private readonly Action<IReadOnlyList<UsbDeviceChange>> _onChanged;
    private readonly Action<Exception>? _onError;
    private readonly ConcurrentQueue<UsbDeviceProbeHint> _hints = new();
    private readonly Lock _gate = new();
    private readonly GCHandle _selfHandle;
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly bool _fireInitialSnapshot;
    private IntPtr _notificationPort;
    private IntPtr _runLoop;
    private IntPtr _runLoopSource;
    private bool _disposed;
    private int _polling, _pending;
    private Exception? _startupError;

    public IOKitUsbHotplugMonitor(
        Func<IReadOnlyList<UsbDeviceProbeHint>, IReadOnlyList<UsbDeviceChange>> pass,
        IReadOnlyList<UsbDeviceInfo> initialDevices,
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        Action<Exception>? onError,
        bool fireInitialSnapshot = false)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new PlatformNotSupportedException("The IOKit USB hotplug monitor is macOS-only.");
        }

        _pass = pass ?? throw new ArgumentNullException(nameof(pass));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onError = onError;
        _fireInitialSnapshot = fireInitialSnapshot;
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        try
        {
            RegisterNotifications();
        }
        catch
        {
            _selfHandle.Free();
            throw;
        }

        _thread = new Thread(RunEventLoop)
        {
            IsBackground = true,
            Name = "UotanStudio.Comm macOS USB hotplug"
        };
        _thread.Start();

        if (!_ready.Wait(StartupTimeout))
        {
            Dispose();
            throw new TimeoutException(
                $"The IOKit USB hotplug run loop did not become ready within {StartupTimeout.TotalSeconds:0.#} s.");
        }

        if (Volatile.Read(ref _startupError) is { } startupError)
        {
            Dispose();
            throw new InvalidOperationException("The IOKit USB hotplug notifications could not be installed.", startupError);
        }

        RaiseInitialSnapshot(initialDevices);
    }

    private void RegisterNotifications()
    {
        _notificationPort = IOKitUsbAPI.IONotificationPortCreate(0);
        if (_notificationPort == IntPtr.Zero)
        {
            throw new IOException("IONotificationPortCreate failed.");
        }

        var refCon = GCHandle.ToIntPtr(_selfHandle);
        foreach (var className in new[] { "IOUSBHostDevice", "IOUSBDevice" })
        {
            RegisterOne(className, MatchedNotification, MatchedCallback, refCon);
            RegisterOne(className, TerminatedNotification, TerminatedCallback, refCon);
        }

        _runLoopSource = IOKitUsbAPI.IONotificationPortGetRunLoopSource(_notificationPort);
        if (_runLoopSource == IntPtr.Zero)
        {
            throw new IOException("IONotificationPortGetRunLoopSource failed.");
        }
    }

    private void RegisterOne(string className, string notification, IOServiceMatchingCallback callback, IntPtr refCon)
    {
        var matching = IOKitUsbAPI.IOServiceMatching(className);
        if (matching == IntPtr.Zero)
        {
            throw new IOException($"IOServiceMatching('{className}') failed.");
        }

        var callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
        var result = IOKitUsbAPI.IOServiceAddMatchingNotification(
            _notificationPort, notification, matching, callbackPointer, refCon, out var iterator);
        if (result != 0)
        {
            throw new IOException($"IOServiceAddMatchingNotification('{className}') failed with 0x{result:X8}.");
        }

        DrainAndRelease(iterator, static (_, _) => { });
    }

    private void RunEventLoop()
    {
        try
        {
            _runLoop = IOKitUsbAPI.CFRunLoopGetCurrent();
            IOKitUsbAPI.CFRunLoopAddSource(_runLoop, _runLoopSource, DefaultRunLoopMode);
            _ready.Set();

            while (!Volatile.Read(ref _disposed))
            {
                IOKitUsbAPI.CFRunLoopRunInMode(DefaultRunLoopMode, 0.25, returnAfterSourceHandled: false);
            }
        }
        catch (Exception ex)
        {
            if (!_ready.IsSet)
            {
                Volatile.Write(ref _startupError, ex);
            }
            else
            {
                _onError?.Invoke(ex);
            }

            UsbTrace.Log($"IOKitUsbHotplugMonitor event loop failed: {ex.GetType().Name}: {ex.Message}");
            _ready.Set();
        }
    }

    private static void OnMatched(IntPtr refCon, IntPtr iterator)
    {
        if (GCHandle.FromIntPtr(refCon).Target is IOKitUsbHotplugMonitor monitor)
        {
            monitor.EnqueueServices(iterator, UsbDeviceChangeKind.Added);
        }
    }

    private static void OnTerminated(IntPtr refCon, IntPtr iterator)
    {
        if (GCHandle.FromIntPtr(refCon).Target is IOKitUsbHotplugMonitor monitor)
        {
            monitor.EnqueueServices(iterator, UsbDeviceChangeKind.Removed);
        }
    }

    private void EnqueueServices(IntPtr iterator, UsbDeviceChangeKind kind)
    {
        DrainAndRelease(iterator, (service, _) =>
        {
            var hint = CreateHint(service, kind);
            if (hint.IsSpecific)
            {
                _hints.Enqueue(hint);
            }
            else
            {
                _hints.Enqueue(new UsbDeviceProbeHint { Kind = kind });
            }
        });
        TriggerPoll();
    }

    private static void DrainAndRelease(IntPtr iterator, Action<IntPtr, IntPtr> visit)
    {
        if (iterator == IntPtr.Zero)
        {
            return;
        }

        try
        {
            uint service;
            while ((service = IOKitUsbAPI.IOIteratorNext(iterator)) != 0)
            {
                var servicePointer = new IntPtr(unchecked((long)service));
                try
                {
                    visit(servicePointer, iterator);
                }
                finally
                {
                    _ = IOKitUsbAPI.IOObjectRelease(servicePointer);
                }
            }
        }
        finally
        {
            _ = IOKitUsbAPI.IOObjectRelease(iterator);
        }
    }

    private static UsbDeviceProbeHint CreateHint(IntPtr service, UsbDeviceChangeKind kind)
    {
        var vendorId = ReadNumber(service, "idVendor");
        var productId = ReadNumber(service, "idProduct");
        var portPath = MacUsbPortIdentity.Observe(service);

        return new UsbDeviceProbeHint
        {
            Kind = kind,
            PhysicalPortPath = portPath,
            VendorId = vendorId,
            ProductId = productId
        };
    }

    private static ushort? ReadNumber(IntPtr service, string name)
    {
        var key = IOKitUsbAPI.CFStringCreateWithCString(IntPtr.Zero, name, IOKitUsbAPI.KCFStringEncodingUTF8);
        if (key == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var property = IOKitUsbAPI.IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
            if (property == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                long value = 0;
                return IOKitUsbAPI.CFNumberGetValue64(property, 4, ref value) && value is >= 0 and <= ushort.MaxValue
                    ? (ushort)value
                    : null;
            }
            finally
            {
                IOKitUsbAPI.CFRelease(property);
            }
        }
        finally
        {
            IOKitUsbAPI.CFRelease(key);
        }
    }

    private void RaiseInitialSnapshot(IReadOnlyList<UsbDeviceInfo> initialDevices)
    {
        if (!_fireInitialSnapshot || initialDevices.Count == 0)
        {
            return;
        }

        var changes = initialDevices
            .Select(device => new UsbDeviceChange { Kind = UsbDeviceChangeKind.Added, Device = device })
            .ToList();
        try
        {
            _onChanged(changes);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
            UsbTrace.Log($"IOKitUsbHotplugMonitor initial snapshot callback failed: {ex.GetType().Name}: {ex.Message}");
        }
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
                    UsbTrace.Log($"IOKitUsbHotplugMonitor poll failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            while (Interlocked.Exchange(ref _pending, 0) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
            if (Volatile.Read(ref _pending) != 0 && !_disposed)
            {
                TriggerPoll();
            }
        }
    }

    private void PollOnce()
    {
        var hints = DrainHints();
        if (hints.Count == 0)
        {
            return;
        }

        IReadOnlyList<UsbDeviceChange> changes;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            changes = _pass(hints);
        }

        if (changes.Count > 0)
        {
            try
            {
                _onChanged(changes);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                UsbTrace.Log($"IOKitUsbHotplugMonitor change callback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private List<UsbDeviceProbeHint> DrainHints()
    {
        var hints = new List<UsbDeviceProbeHint>();
        while (_hints.TryDequeue(out var hint))
        {
            hints.Add(hint);
        }

        return hints;
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

        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            _ = thread.Join(TimeSpan.FromSeconds(2));
        }

        if (_notificationPort != IntPtr.Zero)
        {
            IOKitUsbAPI.IONotificationPortDestroy(_notificationPort);
        }

        while (_hints.TryDequeue(out _))
        {
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _ready.Dispose();
    }

    private static IntPtr CreateDefaultRunLoopMode()
    {
        var mode = IOKitUsbAPI.CFStringCreateWithCString(
            IntPtr.Zero,
            "kCFRunLoopDefaultMode",
            IOKitUsbAPI.KCFStringEncodingUTF8);

        return mode == IntPtr.Zero ? throw new IOException("CFStringCreateWithCString(default run loop mode) failed.") : mode;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void IOServiceMatchingCallback(IntPtr refCon, IntPtr iterator);
}

internal static partial class IOKitUsbAPI
{
    [LibraryImport(IOKit)]
    internal static partial IntPtr IONotificationPortCreate(uint mainPort);

    [LibraryImport(IOKit)]
    internal static partial void IONotificationPortDestroy(IntPtr port);

    [LibraryImport(IOKit)]
    internal static partial IntPtr IONotificationPortGetRunLoopSource(IntPtr port);

    [LibraryImport(IOKit)]
    internal static partial int IOServiceAddMatchingNotification(
        IntPtr notifyPort,
        [MarshalAs(UnmanagedType.LPStr)] string notificationType,
        IntPtr matching,
        IntPtr callback,
        IntPtr refCon,
        out IntPtr iterator);

    [LibraryImport(CoreFoundation)]
    internal static partial IntPtr CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation)]
    internal static partial void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [LibraryImport(CoreFoundation)]
    internal static partial int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool CFNumberGetValue64(IntPtr number, int type, ref long value);
}