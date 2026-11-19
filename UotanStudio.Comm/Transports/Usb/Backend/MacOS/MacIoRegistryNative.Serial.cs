using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// Serial-plane reads on top of the existing I/O Registry layer: a USB device service is matched by its
/// <c>IORegistryEntryID</c>, then its descendants are walked for an <c>IOSerialBSDClient</c> service whose
/// <c>IODialinDevice</c> (preferred) or <c>IOCalloutDevice</c> property names the character device. Locating the
/// port never opens the USB device: the node is resolved from registry properties only, so the fallback cannot
/// contend with whoever already holds the device.
/// <para>在已有 I/O Registry 层之上的串口平面读取：先按 <c>IORegistryEntryID</c> 匹配 USB 设备服务，再向下遍历其
/// 后代寻找 <c>IOSerialBSDClient</c> 服务，由它的 <c>IODialinDevice</c>（优先）或 <c>IOCalloutDevice</c> 属性给出
/// 字符设备名。定位端口的过程绝不打开 USB 设备：节点只按注册表属性解析，因此该回退不会与已持有设备的一方争用。</para>
/// </summary>
/// <remarks>
/// macOS reports <c>IOKit:&lt;registry entry id&gt;</c> as a device path, so identity is resolved by matching that
/// numeric id rather than by parsing service paths. A device with no such descendant returns <see langword="null"/>,
/// which is exactly what a device served by an exclusive USB driver looks like. Every iterator and service handle is
/// released on every path; the walk never throws past the documented interop failures.
/// <para>macOS 把 <c>IOKit:&lt;注册表条目 ID&gt;</c> 作为设备路径上报，因此身份按该数字 ID 匹配，而非解析服务路径。
/// 没有该后代的设备返回 <see langword="null"/>，这正是被独占 USB 驱动接管的设备的样子。每条路径上都释放迭代器与
/// 服务句柄；遍历不会抛出已记录的互操作失败之外的异常。</para>
/// </remarks>
internal static partial class MacIoRegistryNative
{
    private const string SerialClass = "IOSerialBSDClient";
    private const string DialinKey = "IODialinDevice";
    private const string CalloutKey = "IOCalloutDevice";
    private const string RegistryEntryIdProperty = "IORegistryEntryID";

    private static readonly string[] SerialDeviceClassNames = ["IOUSBHostDevice", "IOUSBDevice"];

    /// <summary>
    /// Parses the registry entry id out of the <c>IOKit:&lt;id&gt;</c> device path macOS reports for an endpoint,
    /// accepting only the exact documented prefix and a fully numeric tail.
    /// <para>从 macOS 为端点上报的 <c>IOKit:&lt;id&gt;</c> 设备路径中解析注册表条目 ID，只接受文档记载的确切前缀
    /// 与纯数字尾部。</para>
    /// </summary>
    /// <param name="devicePath">The enumerated device path.<para>已枚举的设备路径。</para></param>
    /// <returns>The numeric entry id, or <see langword="null"/> when the path is not that shape.
    /// <para>数字条目 ID；路径不是该形态时为 <see langword="null"/>。</para></returns>
    internal static ulong? TryParseRegistryEntryId(string? devicePath)
    {
        const string prefix = "IOKit:";
        if (string.IsNullOrWhiteSpace(devicePath) ||
            !devicePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var tail = devicePath[prefix.Length..];
        return tail.Length > 0 && ulong.TryParse(tail, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    /// <summary>
    /// Resolves the BSD character device the serial descendant of the described USB device publishes, preferring the
    /// dial-in node so an already-open callout stays free.
    /// <para>解析所描述 USB 设备的串口后代公布的 BSD 字符设备；优先 dial-in 节点，以免占用已被打开的 callout。</para>
    /// </summary>
    /// <param name="devicePath">The enumerated device path carrying the registry entry id.
    /// <para>携带注册表条目 ID 的已枚举设备路径。</para></param>
    /// <returns>A character device path such as <c>/dev/cu.usbmodem14101</c>, or <see langword="null"/>.
    /// <para>形如 <c>/dev/cu.usbmodem14101</c> 的字符设备路径；或 <see langword="null"/>。</para></returns>
    internal static string? TryFindSerialDeviceNode(string? devicePath)
    {
        if (!IsAvailable || TryParseRegistryEntryId(devicePath) is not { } entryId)
        {
            return null;
        }

        try
        {
            return FindSerialDevice(entryId);
        }
        catch (Exception error) when (IsIgnorableInteropFailure(error))
        {
            UsbTrace.LogFormatted($"MTK macOS serial fallback: IOKit read failed: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private static string? FindSerialDevice(ulong entryId)
    {
        foreach (var className in SerialDeviceClassNames)
        {
            var matching = IOServiceMatching(className);
            if (matching == IntPtr.Zero)
            {
                continue;
            }

            var iterator = IntPtr.Zero;
            try
            {
                if (IOServiceGetMatchingServices(IntPtr.Zero, matching, out var acquired) != Success ||
                    acquired == IntPtr.Zero)
                {
                    continue;
                }

                matching = IntPtr.Zero;
                iterator = acquired;

                for (var next = IOIteratorNext(iterator); next != 0; next = IOIteratorNext(iterator))
                {
                    var service = new IntPtr(next);
                    try
                    {
                        if (ReadNumber(service, RegistryEntryIdProperty) is not { } raw ||
                            unchecked((ulong)raw) != entryId)
                        {
                            continue;
                        }

                        if (FindSerialDescendant(service, depth: 0) is { } device)
                        {
                            return device;
                        }
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

        return null;
    }

    private static string? FindSerialDescendant(IntPtr service, int depth)
    {
        if (depth > MaxClimbDepth ||
            IORegistryEntryCreateIterator(service, ServicePlane, 0, out var iterator) != Success ||
            iterator == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            for (var next = IOIteratorNext(iterator); next != 0; next = IOIteratorNext(iterator))
            {
                var child = new IntPtr(next);
                try
                {
                    if (string.Equals(ReadString(child, "IOClass"), SerialClass, StringComparison.Ordinal))
                    {
                        if (ReadString(child, DialinKey) is { Length: > 0 } dialin)
                        {
                            return dialin;
                        }

                        if (ReadString(child, CalloutKey) is { Length: > 0 } callout)
                        {
                            return callout;
                        }
                    }

                    if (FindSerialDescendant(child, depth + 1) is { } nested)
                    {
                        return nested;
                    }
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

        return null;
    }
}