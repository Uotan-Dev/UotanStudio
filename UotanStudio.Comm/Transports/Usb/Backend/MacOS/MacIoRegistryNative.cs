using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// The native enumeration layer for the macOS I/O Registry USB plane: it performs registry reads only
/// (<c>IOServiceGetMatchingServices</c>, <c>IORegistryEntryCreateIterator</c>,
/// <c>IORegistryEntryGetParentEntry</c>, <c>IORegistryEntryCreateCFProperty</c>,
/// <c>IORegistryEntryGetPath</c>) and turns each service into a <see cref="MacIoRegistryEntry"/> property
/// bag. Hard constraint: it never opens a device, never claims an interface, and never issues a control
/// transfer or MIG call, because a wedged device would hang the USB tree window — the same risk the
/// <see cref="UsbHostFinder"/> header documents when iterating every interface service. Every
/// <c>io_iterator_t</c>, <c>io_service_t</c>, CF key and CF value is released with try/finally on every
/// path, so nothing leaks across refresh cycles.
/// <para>macOS I/O Registry USB 平面的原生枚举层：只读取注册表属性（<c>IOServiceGetMatchingServices</c>、
/// <c>IORegistryEntryCreateIterator</c>、<c>IORegistryEntryGetParentEntry</c>、
/// <c>IORegistryEntryCreateCFProperty</c>、<c>IORegistryEntryGetPath</c>），把每个服务读成
/// <see cref="MacIoRegistryEntry"/> 属性袋。硬约束：绝不打开设备、绝不申请接口、绝不发起控制传输或
/// MIG 调用，因为卡死的设备会挂住 USB 树窗口；这与 <see cref="UsbHostFinder"/> 头部注释记录的“迭代全部
/// 接口服务读取属性会在设备卡死时无限阻塞”是同一风险的规避。所有 <c>io_iterator_t</c>、
/// <c>io_service_t</c>、CF 键与 CF 值都在每条路径上用 try/finally 释放，跨刷新周期不泄漏。</para>
/// </summary>
internal static partial class MacIoRegistryNative
{
    private const int Success = 0;
    private const string ServicePlane = "IOService";
    private const int MaxClimbDepth = 32;
    private const int MaxPathLength = 512;
    private const int MaxStringLength = 512;
    private const int KCFNumberSInt64Type = 3;

    private static readonly string[] DeviceClassNames = ["IOUSBHostDevice", "IOUSBDevice"];
    private static readonly string[] ProductNameKeys = ["USB Product Name", "kUSBProductString"];
    private static readonly string[] VendorNameKeys = ["USB Vendor Name"];
    private static readonly string[] SerialNumberKeys = ["USB Serial Number", "serial number"];
    private static readonly string[] InterfaceNameKeys = ["IOUSBInterfaceName", "kIOUSBInterfaceName"];
    private static readonly string[] SpeedKeys = ["kUSBDeviceSpeed", "device-speed"];

    private static readonly Lazy<bool> RegistryReachable = new(
        () => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
              NativeLibrary.TryLoad(IOKitUsbAPI.IOKit, out _),
        isThreadSafe: true);

    /// <summary>
    /// Whether the I/O Registry can be walked from this process: macOS and the IOKit framework resolves.
    /// <para>能否从本进程遍历 I/O Registry：处于 macOS 且 IOKit 框架可解析。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when registry reads are possible. <para>可执行注册表读取时为 <see langword="true"/>。</para>
    /// </value>
    internal static bool IsAvailable => RegistryReachable.Value;

    /// <summary>
    /// Walks the USB plane and returns one property bag per visible node: device services from
    /// <c>IOUSBHostDevice</c> and the legacy <c>IOUSBDevice</c>, each ancestor climbed until a host
    /// controller, and the interface children of every device. A single unreadable property degrades to
    /// <see langword="null"/>; the walk never throws.
    /// <para>遍历 USB 平面，为每个可见节点返回一个属性袋：来自 <c>IOUSBHostDevice</c> 与遗留
    /// <c>IOUSBDevice</c> 的设备服务、各自向上爬到主机控制器为止的祖先，以及每个设备的接口子节点。
    /// 单个不可读属性降级为 <see langword="null"/>；遍历永不抛出。</para>
    /// </summary>
    /// <returns>
    /// The property bags keyed by registry path, empty when nothing is visible. <para>以注册表路径为键的属性袋；无可见内容时为空。</para>
    /// </returns>
    internal static IReadOnlyList<MacIoRegistryEntry> Enumerate()
    {
        var entries = new Dictionary<string, MacIoRegistryEntry>(StringComparer.Ordinal);
        if (!IsAvailable)
        {
            return [];
        }

        try
        {
            foreach (var className in DeviceClassNames)
            {
                SeedDeviceServices(className, entries);
            }
        }
        catch (Exception error) when (IsIgnorableInteropFailure(error))
        {
            return [];
        }

        return [.. entries.Values];
    }

    private static void SeedDeviceServices(string className, Dictionary<string, MacIoRegistryEntry> entries)
    {
        var matching = IOServiceMatching(className);
        if (matching == IntPtr.Zero)
        {
            return;
        }

        var iterator = IntPtr.Zero;
        try
        {
            if (IOServiceGetMatchingServices(IntPtr.Zero, matching, out var acquired) != Success ||
                acquired == IntPtr.Zero)
            {
                return;
            }

            matching = IntPtr.Zero;
            iterator = acquired;

            for (var next = IOIteratorNext(iterator); next != 0; next = IOIteratorNext(iterator))
            {
                var service = new IntPtr(next);
                try
                {
                    SeedSingleDevice(service, entries);
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }
        }
        finally
        {
            if (matching != IntPtr.Zero)
            {
                CFRelease(matching);
            }

            if (iterator != IntPtr.Zero)
            {
                IOObjectRelease(iterator);
            }
        }
    }

    private static void SeedSingleDevice(IntPtr service, Dictionary<string, MacIoRegistryEntry> entries)
    {
        var instanceId = ReadPath(service);
        if (instanceId is not { Length: > 0 })
        {
            return;
        }

        if (!entries.ContainsKey(instanceId))
        {
            entries[instanceId] = ReadDeviceEntry(service, instanceId, null);
        }

        ClimbAncestors(service, instanceId, entries);
        EnumerateInterfaces(service, instanceId, entries);
    }

    private static void ClimbAncestors(IntPtr service, string servicePath,
        Dictionary<string, MacIoRegistryEntry> entries)
    {
        var current = service;
        var currentOwned = false;
        var childId = servicePath;

        try
        {
            for (var depth = 0; depth < MaxClimbDepth; depth++)
            {
                if (IORegistryEntryGetParentEntry(current, ServicePlane, out var parent) != Success ||
                    parent == IntPtr.Zero)
                {
                    break;
                }

                var parentPath = ReadPath(parent);
                var ancestor = parentPath is { Length: > 0 }
                    ? ReadDeviceEntry(parent, parentPath, null)
                    : null;

                if (ancestor is not null && parentPath is { Length: > 0 } path)
                {
                    if (!entries.ContainsKey(path))
                    {
                        entries[path] = ancestor;
                    }

                    if (entries.TryGetValue(childId, out var child))
                    {
                        entries[childId] = child with { ParentInstanceId = path };
                    }

                    childId = path;
                }

                if (currentOwned)
                {
                    IOObjectRelease(current);
                }

                current = parent;
                currentOwned = true;

                var reachedController = ancestor is not null &&
                    MacIoRegistryTopology.ResolveKind(ancestor.IOClass, ancestor.DeviceClass) ==
                        UsbTopologyNodeKind.HostController;
                if (ancestor is null || reachedController)
                {
                    break;
                }
            }
        }
        finally
        {
            if (currentOwned)
            {
                IOObjectRelease(current);
            }
        }
    }

    private static void EnumerateInterfaces(IntPtr service, string devicePath,
        Dictionary<string, MacIoRegistryEntry> entries)
    {
        if (IORegistryEntryCreateIterator(service, ServicePlane, 0, out var iterator) != Success ||
            iterator == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (var next = IOIteratorNext(iterator); next != 0; next = IOIteratorNext(iterator))
            {
                var child = new IntPtr(next);
                try
                {
                    var ioClass = ReadString(child, "IOClass");
                    if (ioClass is not { Length: > 0 } name ||
                        !name.Contains("Interface", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var instanceId = ReadPath(child) ?? $"{devicePath}#interface:{next:X8}";
                    if (entries.ContainsKey(instanceId))
                    {
                        continue;
                    }

                    entries[instanceId] = ReadInterfaceEntry(child, instanceId, devicePath, name);
                }
                finally
                {
                    IOObjectRelease(child);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }
    }

    private static MacIoRegistryEntry ReadDeviceEntry(IntPtr service, string instanceId, string? parentId)
        => new()
        {
            InstanceId = instanceId,
            ParentInstanceId = parentId,
            IOClass = ReadString(service, "IOClass"),
            VendorId = ToUInt16(ReadNumber(service, "idVendor")),
            ProductId = ToUInt16(ReadNumber(service, "idProduct")),
            BcdDevice = ToUInt16(ReadNumber(service, "bcdDevice")),
            DeviceClass = ToByte(ReadNumber(service, "bDeviceClass")),
            SerialNumber = ReadFirstString(service, SerialNumberKeys),
            VendorName = ReadFirstString(service, VendorNameKeys),
            ProductName = ReadFirstString(service, ProductNameKeys),
            Speed = ReadFirstString(service, SpeedKeys)
        };

    private static MacIoRegistryEntry ReadInterfaceEntry(IntPtr service, string instanceId, string parentId,
        string ioClass)
        => new()
        {
            InstanceId = instanceId,
            ParentInstanceId = parentId,
            IOClass = ioClass,
            InterfaceClass = ToByte(ReadNumber(service, "bInterfaceClass")),
            InterfaceSubClass = ToByte(ReadNumber(service, "bInterfaceSubClass")),
            InterfaceProtocol = ToByte(ReadNumber(service, "bInterfaceProtocol")),
            InterfaceNumber = ToByte(ReadNumber(service, "bInterfaceNumber")),
            InterfaceName = ReadFirstString(service, InterfaceNameKeys)
        };

    private static string? ReadFirstString(IntPtr service, string[] keys)
    {
        foreach (var key in keys)
        {
            if (ReadString(service, key) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static ushort? ToUInt16(long? value)
        => value is { } number && number is >= 0 and <= ushort.MaxValue
            ? unchecked((ushort)number)
            : null;

    private static byte? ToByte(long? value)
        => value is { } number && number is >= 0 and <= byte.MaxValue
            ? unchecked((byte)number)
            : null;

    private static long? ReadNumber(IntPtr service, string name)
    {
        var key = CreateKey(name);
        if (key == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var value = IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
            if (value == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (CFGetTypeID(value) != CFNumberGetTypeID())
                {
                    return null;
                }

                var number = 0L;
                return CFNumberGetValue(value, KCFNumberSInt64Type, ref number) ? number : null;
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(key);
        }
    }

    private static string? ReadString(IntPtr service, string name)
    {
        var key = CreateKey(name);
        if (key == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var value = IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
            if (value == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (CFGetTypeID(value) != CFStringGetTypeID())
                {
                    return null;
                }

                var buffer = new byte[MaxStringLength];
                unsafe
                {
                    fixed (byte* pointer = buffer)
                    {
                        if (!CFStringGetCString(value, (IntPtr)pointer, buffer.Length,
                                IOKitUsbAPI.KCFStringEncodingUTF8))
                        {
                            return null;
                        }
                    }
                }

                var length = Array.IndexOf(buffer, (byte)0);
                return length <= 0 ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, length);
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(key);
        }
    }

    private static string? ReadPath(IntPtr service)
    {
        var buffer = new byte[MaxPathLength];
        int result;
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                result = IORegistryEntryGetPath(service, ServicePlane, (IntPtr)pointer);
            }
        }

        if (result != Success)
        {
            return null;
        }

        var length = Array.IndexOf(buffer, (byte)0);
        return length <= 0
            ? null
            : System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static IntPtr CreateKey(string name)
        => CFStringCreateWithCString(IntPtr.Zero, name, IOKitUsbAPI.KCFStringEncodingUTF8);

    private static bool IsIgnorableInteropFailure(Exception error)
        => error is DllNotFoundException or EntryPointNotFoundException or ArgumentException
            or NotSupportedException or InvalidOperationException;

    [LibraryImport(IOKitUsbAPI.IOKit, EntryPoint = "IOServiceMatching")]
    private static partial IntPtr IOServiceMatching(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(IOKitUsbAPI.IOKit, EntryPoint = "IOServiceGetMatchingServices")]
    private static partial int IOServiceGetMatchingServices(IntPtr masterPort, IntPtr matching,
        out IntPtr existing);

    [LibraryImport(IOKitUsbAPI.IOKit, EntryPoint = "IOIteratorNext")]
    private static partial uint IOIteratorNext(IntPtr iterator);

    [LibraryImport(IOKitUsbAPI.IOKit, EntryPoint = "IOObjectRelease")]
    private static partial int IOObjectRelease(IntPtr obj);

    [LibraryImport(IOKitUsbAPI.IOKit, EntryPoint = "IORegistryEntryCreateCFProperty")]
    private static partial IntPtr IORegistryEntryCreateCFProperty(IntPtr entry, IntPtr key, IntPtr allocator,
        uint options);

    [LibraryImport(IOKitUsbAPI.IOKit, EntryPoint = "IORegistryEntryCreateIterator")]
    private static partial int IORegistryEntryCreateIterator(IntPtr entry,
        [MarshalAs(UnmanagedType.LPStr)] string plane, uint options, out IntPtr iterator);

    [LibraryImport(IOKitUsbAPI.IOKit, EntryPoint = "IORegistryEntryGetParentEntry")]
    private static partial int IORegistryEntryGetParentEntry(IntPtr entry,
        [MarshalAs(UnmanagedType.LPStr)] string plane, out IntPtr parent);

    [LibraryImport(IOKitUsbAPI.IOKit, EntryPoint = "IORegistryEntryGetPath")]
    private static partial int IORegistryEntryGetPath(IntPtr entry,
        [MarshalAs(UnmanagedType.LPStr)] string plane, IntPtr path);

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
    private static partial IntPtr CFStringCreateWithCString(IntPtr alloc,
        [MarshalAs(UnmanagedType.LPStr)] string cStr, int encoding);

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void CFRelease(IntPtr obj);

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFGetTypeID")]
    private static partial IntPtr CFGetTypeID(IntPtr cf);

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFNumberGetTypeID")]
    private static partial IntPtr CFNumberGetTypeID();

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFStringGetTypeID")]
    private static partial IntPtr CFStringGetTypeID();

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(IntPtr number, int theType, ref long valuePtr);

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFStringGetCString")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(IntPtr theString, IntPtr buffer, int maxLength, int encoding);
}