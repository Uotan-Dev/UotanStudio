using System.Globalization;

namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Builds stable identity keys for USB devices.
/// <para>为 USB 设备构建稳定标识键。</para>
/// </summary>
internal static class UsbDeviceIdentity
{
    /// <summary>
    /// Index of the serial segment in a <see cref="BuildKey"/>-produced key.
    /// <para><see cref="BuildKey"/> 生成的键中序列号段的下标。</para>
    /// </summary>
    private const int SerialSegmentIndex = 7;

    /// <summary>
    /// Index of the device-path segment in a <see cref="BuildKey"/>-produced key.
    /// <para><see cref="BuildKey"/> 生成的键中设备路径段的下标。</para>
    /// </summary>
    private const int DevicePathSegmentIndex = 8;

    /// <summary>
    /// Number of segments in a <see cref="BuildKey"/>-produced key.
    /// <para><see cref="BuildKey"/> 生成的键的分段数。</para>
    /// </summary>
    private const int SegmentCount = 9;

    /// <summary>
    /// Matches a discovery key against the key of an opened session. The serial segment may
    /// differ in casing (Windows registry/device-instance casing vs the USB descriptor casing
    /// of the same serial — verified on real hardware), and a discovery key with an absent
    /// serial matches an opened session whether that session left the serial absent or
    /// enriched it (Linux usbfs never reads the serial at discovery time; it is only read by
    /// a control transfer inside <c>CreateHandle</c>, so a device with no serial string
    /// descriptor stays absent on both sides). Every other segment, including the exact
    /// device path, must be identical.
    /// <para>匹配发现键与已打开会话的键。序列号段允许大小写差异（Windows 注册表大小写与
    /// USB 描述符大小写不同——真机验证），且发现时缺序列号的键与已打开会话匹配时，
    /// 该会话的序列号既允许仍为空、也允许已被补全（Linux usbfs 在发现期从不读序列号，
    /// 只在 <c>CreateHandle</c> 内经控制传输读出，因此没有序列号字符串描述符的设备
    /// 在两侧都为空）。其余各段（含精确设备路径）必须完全一致。</para>
    /// Identity basis while the serial is absent: with no serial on either side the device
    /// path segment is the sole evidence that the opened handle is the enumerated device, so
    /// a discovery key that also lacks a device path matches nothing except a byte-identical
    /// key. Byte-identical keys are accepted by an ordinal short-circuit before any segment
    /// inspection, which is what keeps a backend that reports neither serial nor path usable.
    /// <para>序列号缺失时的身份依据：两侧都无序列号时，设备路径段是"已打开句柄就是被枚举
    /// 设备"的唯一证据，因此同时缺设备路径的发现键除逐字节相同的键之外不匹配任何键。
    /// 逐字节相同的键在检查任何分段之前由序数相等短路接受，这正是既不报序列号也不报路径
    /// 的后端仍可用的原因。</para>
    /// Residual risk: device paths are recycled by the operating system — Linux usbfs uses
    /// <c>/dev/bus/usb/BBB/DDD</c> and libusb uses <c>Bus X Device Y: VVVV:PPPP</c>, both of
    /// which the kernel reassigns after an unplug/replug or re-enumeration. While the serial
    /// is absent this matcher therefore cannot distinguish "same device" from "a different
    /// device now sitting on the recycled path"; the risk is inherent to path-based matching
    /// and is not introduced by accepting an absent serial, but an absent serial removes the
    /// only corroborating segment. The complete fix is to corroborate with best-evidence
    /// physical identity (container id first, physical port chain second, never VID/PID or
    /// enumeration order), which lives in the device-discovery layer above this transport
    /// abstraction and so is deliberately not referenced here.
    /// <para>残留风险：设备路径会被操作系统回收复用——Linux usbfs 使用
    /// <c>/dev/bus/usb/BBB/DDD</c>，libusb 使用 <c>Bus X Device Y: VVVV:PPPP</c>，
    /// 两者在拔插或重新枚举后都会被内核重新分配。因此序列号缺失时本匹配器无法区分
    /// "同一台设备"与"另一台设备恰好占用了被回收的路径"；该风险源自基于路径的匹配本身，
    /// 并非由接受空序列号引入，但空序列号移除了唯一可作佐证的段。彻底解法是用最优证据
    /// 物理身份（容器 ID 优先、物理端口链次之，绝不使用 VID/PID 或枚举顺序）加以佐证，
    /// 该原语位于本传输抽象之上的设备发现层，故此处有意不予引用。</para>
    /// </summary>
    /// <param name="requested">A discovery key produced by <see cref="BuildKey"/>. <para>由 <see cref="BuildKey"/> 生成的发现键。</para></param>
    /// <param name="opened">The key of an opened session, also produced by <see cref="BuildKey"/>. <para>已打开会话的键，同样由 <see cref="BuildKey"/> 生成。</para></param>
    /// <returns><see langword="true"/> when both keys denote the same device on the same physical path. <para>两键指向同一路径上的同一设备时返回 <see langword="true"/>。</para></returns>
    internal static bool MatchesOpenedKey(string requested, string opened)
    {
        if (string.Equals(requested, opened, StringComparison.Ordinal)) return true;
        var before = requested.Split('|', SegmentCount);
        var after = opened.Split('|', SegmentCount);
        if (before.Length != SegmentCount || after.Length != SegmentCount) return false;
        for (var i = 0; i < SegmentCount; i++)
        {
            if (i == SerialSegmentIndex)
            {
                if (!SerialSegmentsMatch(before, after)) return false;
            }
            else if (!string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares the serial segments of two keys. A serial present at discovery must be
    /// re-found on the opened session, ignoring the casing difference between the Windows
    /// device-instance spelling and the USB descriptor spelling of the same serial. A serial
    /// absent at discovery accepts either state on the opened session, provided the discovery
    /// key carried a device path to anchor the identity; without that path there is no
    /// remaining evidence, so only the byte-identical short-circuit in
    /// <see cref="MatchesOpenedKey"/> can match.
    /// <para>比较两个键的序列号段。发现时存在的序列号必须在已打开会话上重新找到，
    /// 并忽略同一序列号在 Windows 设备实例拼写与 USB 描述符拼写之间的大小写差异。
    /// 发现时缺失的序列号接受已打开会话上的任一状态，前提是发现键携带了用于锚定身份的
    /// 设备路径；缺少该路径时已无任何剩余证据，只有 <see cref="MatchesOpenedKey"/>
    /// 中逐字节相同的短路才能匹配。</para>
    /// </summary>
    /// <param name="before">The discovery key segments. <para>发现键的分段。</para></param>
    /// <param name="after">The opened-session key segments. <para>已打开会话键的分段。</para></param>
    /// <returns><see langword="true"/> when the serial segments are consistent with one physical device. <para>序列号段与同一台物理设备一致时返回 <see langword="true"/>。</para></returns>
    private static bool SerialSegmentsMatch(string[] before, string[] after)
    {
        if (before[SerialSegmentIndex].Length != 0)
        {
            return string.Equals(before[SerialSegmentIndex], after[SerialSegmentIndex], StringComparison.OrdinalIgnoreCase);
        }

        return before[DevicePathSegmentIndex].Length != 0;
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
            ? info.ApiName + ":" + NormalizeInterfacePath(info.DevicePath) : info.SerialNumber;
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
    /// Collapses the per-driver <c>#{interface-class-guid}</c> suffix on Windows device
    /// interface paths so the same physical function registered under several interface
    /// GUIDs (e.g. a fastboot node without a serial exposing WinUSB under two GUIDs) maps
    /// to one physical identity. The instance id before the suffix (including the
    /// <c>&amp;mi_</c> interface number on composite devices) is preserved, so genuinely
    /// distinct devices or distinct interfaces never merge.
    /// <para>去除 Windows 设备接口路径末尾的 <c>#{接口类 GUID}</c> 后缀，使同一物理功能在同一
    /// 节点下注册多个接口 GUID（例如无序列号的 fastboot 节点在 WinUSB 下挂两个 GUID）映射为
    /// 单一物理标识。后缀之前的实例 ID（含复合设备上的 <c>&amp;mi_</c> 接口号）被保留，
    /// 因此真正不同的设备或不同接口不会被合并。</para>
    /// </summary>
    private static string NormalizeInterfacePath(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return devicePath;
        var marker = devicePath.IndexOf("#{", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? devicePath : devicePath[..marker];
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
            if (!string.IsNullOrEmpty(parts[7]) && parts[7] != "UNKNOWN") filter.SerialNumber = parts[7];
        }
        return filter;
    }
}