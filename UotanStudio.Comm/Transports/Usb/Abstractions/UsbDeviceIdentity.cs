using System.Globalization;

namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Builds stable identity keys for USB devices.
/// <para>为 USB 设备构建稳定标识键。</para>
/// </summary>
internal static class UsbDeviceIdentity
{
    /// <summary>Matches a discovery key after opening enriches an initially unavailable serial.</summary>
    internal static bool MatchesOpenedKey(string requested, string opened)
    {
        if (string.Equals(requested, opened, StringComparison.Ordinal)) return true;
        var before = requested.Split('|', 9);
        var after = opened.Split('|', 9);
        // Never weaken a known serial or a physical path/interface constraint.
        return before.Length == 9 && after.Length == 9 && before[7].Length == 0 &&
            before[8].Length != 0 && before[8] == after[8] &&
            before.AsSpan(0, 7).SequenceEqual(after.AsSpan(0, 7));
    }

    /// <summary>
    /// Creates a stable key that can be used for reopen, monitoring, or deduplication.
    /// <para>创建可用于重连、监视或去重的稳定键。</para>
    /// </summary>
    /// <param name="info">The device metadata. <para>设备元数据。</para></param>
    /// <returns>A stable identity string. <para>稳定标识字符串。</para></returns>
    public static string BuildKey(UsbDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var serial = info.SerialNumber ?? string.Empty;
        var devicePath = info.DevicePath ?? string.Empty;
        var interfaceClass = info.InterfaceClass?.ToString("X2") ?? string.Empty;
        var interfaceSubClass = info.InterfaceSubClass?.ToString("X2") ?? string.Empty;
        var interfaceProtocol = info.InterfaceProtocol?.ToString("X2") ?? string.Empty;

        return string.Join("|", new[]
        {
            info.ApiName,
            info.SourceApiKind.ToString(),
            info.VendorId.ToString("X4"),
            info.ProductId.ToString("X4"),
            interfaceClass,
            interfaceSubClass,
            interfaceProtocol,
            serial,
            devicePath
        });
    }

    /// <summary>
    /// Builds a physical identity key that is independent of the backend and the device
    /// path, so the same physical device is deduplicated across backends (native + libusb)
    /// in monitoring. Based on VID/PID/interface class+subclass/serial — the interface
    /// PROTOCOL is deliberately excluded so a mode/protocol change on the same interface
    /// is reported as a metadata change (Changed) rather than as a remove+add.
    /// <para>构建与后端和设备路径无关的物理身份键，使同一物理设备在监控中可跨后端
    /// （native + libusb）去重。仅基于 VID/PID/接口类+子类/序列号——有意排除接口协议号，
    /// 使同一接口上的模式/协议变化被上报为元数据变化（Changed）而非移除+新增。</para>
    /// Devices without a reliable serial retain their backend and physical path so
    /// two identical unlabelled devices cannot collapse onto a single identity.
    /// </summary>
    /// <param name="info">The device metadata. <para>设备元数据。</para></param>
    /// <returns>A backend-independent physical identity string. <para>与后端无关的物理标识字符串。</para></returns>
    public static string BuildPhysicalKey(UsbDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var serial = string.IsNullOrEmpty(info.SerialNumber) || info.SerialNumber == "UNKNOWN"
            ? info.ApiName + ":" + info.DevicePath : info.SerialNumber;
        var interfaceClass = info.InterfaceClass?.ToString("X2") ?? string.Empty;
        var interfaceSubClass = info.InterfaceSubClass?.ToString("X2") ?? string.Empty;

        return string.Join("|", new[]
        {
            info.VendorId.ToString("X4"),
            info.ProductId.ToString("X4"),
            interfaceClass,
            interfaceSubClass,
            serial
        });
    }

    /// <summary>
    /// Rebuilds a <see cref="UsbDeviceFilter"/> from a <see cref="BuildKey"/>-produced key
    /// (segments: apiName|apiKind|VID|PID|ifClass|ifSub|ifProt|serial|path), carrying the
    /// VID/PID and the interface triple forward so a by-key open binds the SAME interface
    /// the key was produced from. A key whose interface segment is FF|42|01 (an ADB
    /// interface-filtered enumeration) must not fall back to the first bulk interface
    /// (FF|FF|00) of a composite device.
    /// <para>从 <see cref="BuildKey"/> 生成的键（分段：apiName|apiKind|VID|PID|ifClass|ifSub|
    /// ifProt|serial|path）重建 <see cref="UsbDeviceFilter"/>，携带 VID/PID 与接口三元组，
    /// 使按键打开时绑定与生成该键时相同的接口。接口段为 FF|42|01 的键（ADB 接口过滤器枚举）
    /// 不得回退到复合设备第一个 bulk 接口（FF|FF|00）。</para>
    /// </summary>
    /// <param name="deviceKey">A key produced by <see cref="BuildKey"/>. <para>由 <see cref="BuildKey"/> 生成的键。</para></param>
    /// <returns>A filter, or <c>null</c> when the key cannot be parsed. <para>过滤器；无法解析时返回 <c>null</c>。</para></returns>
    public static UsbDeviceFilter? TryParseKeyFilter(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return null;
        }

        // Split into at most 9 segments; VID/PID/interface triple are segments 2..6.
        // <para>最多拆为 9 段；VID/PID/接口三元组为第 2..6 段。</para>
        var parts = deviceKey.Split('|', 9);
        if (parts.Length < 7)
        {
            return null;
        }

        var filter = new UsbDeviceFilter();
        if (ushort.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid))
        {
            filter.VendorId = vid;
        }

        if (ushort.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
        {
            filter.ProductId = pid;
        }

        if (byte.TryParse(parts[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ifClass))
        {
            filter.InterfaceClass = ifClass;
        }

        if (byte.TryParse(parts[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ifSubClass))
        {
            filter.InterfaceSubClass = ifSubClass;
        }

        if (byte.TryParse(parts[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ifProtocol))
        {
            filter.InterfaceProtocol = ifProtocol;
        }

        if (parts.Length == 9)
        {
            filter.DevicePathEquals = parts[8];
            filter.DevicePathContains = parts[8];
            // Opening may enrich an absent serial; the physical path remains mandatory.
            if (!string.IsNullOrEmpty(parts[7]) && parts[7] != "UNKNOWN") filter.SerialNumber = parts[7];
        }
        return filter;
    }
}
