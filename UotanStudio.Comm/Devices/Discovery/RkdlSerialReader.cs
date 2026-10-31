using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Builds the display identity of a discovered Rockchip bootrom/loader device, aligned with
/// RKDevTool: loader-mode devices get the chip name read via one short READ_CHIP_INFO probe, and
/// every row carries a location suffix from the observed USB topology so two identical boards are
/// told apart. Maskrom ports cannot answer chip info, so their identity keeps the maskrom label
/// plus the location suffix, never a guessed chip name.
/// <para>为发现的瑞芯微 bootrom/loader 设备构建显示身份，与 RKDevTool 对齐：loader 模式的设备
/// 经一次短暂 READ_CHIP_INFO 探测读取芯片名，且每一行都带来自已观测 USB 拓扑的位置后缀，
/// 使两块相同的开发板可区分。Maskrom 端口无法应答芯片信息，因此其身份保持 maskrom 标签加
/// 位置后缀，绝不猜测芯片名。</para>
/// </summary>
internal static class RkdlSerialReader
{
    /// <summary>
    /// Probes one discovered Rockchip device for its display serial: the chip name when the device
    /// answers READ_CHIP_INFO in loader mode, the maskrom label otherwise, and the location suffix
    /// in every case.
    /// <para>探测单个已发现瑞芯微设备的显示序列号：loader 模式下设备应答 READ_CHIP_INFO 时为芯片名，
    /// 否则为 maskrom 标签，且任何情况下都带位置后缀。</para>
    /// </summary>
    /// <param name="kit">The communication kit used to open the USB session by device key.
    /// <para>用于按设备键打开 USB 会话的通信套件。</para></param>
    /// <param name="device">The discovered USB device descriptor. <para>已发现的 USB 设备描述符。</para></param>
    /// <param name="failureSink">
    /// Optional diagnostics sink that receives the probe-failure line verbatim; when <see langword="null"/>,
    /// the failure is reported through <see cref="UsbTrace.LogError(string)"/> instead.
    /// <para>可选的诊断汇，逐字接收探测失败行；为 <see langword="null"/> 时失败改由
    /// <see cref="UsbTrace.LogError(string)"/> 上报。</para>
    /// </param>
    /// <returns>
    /// The display serial, or <c>null</c> when no session matches, the device is not a rockusb
    /// interface, or no location evidence was observed.
    /// <para>显示序列号；无会话匹配、设备非 rockusb 接口或未观测到位置证据时返回 <c>null</c>。</para>
    /// </returns>
    internal static string? Read(ICommKit kit, UsbDeviceInfo device, IUsbLogger? failureSink = null)
    {
        ArgumentNullException.ThrowIfNull(kit);
        ArgumentNullException.ThrowIfNull(device);
        var location = FormatLocation(device);
        if (location is null)
        {
            return null;
        }

        RkdlSession? session = null;
        IUsbDeviceSession? usb = null;
        try
        {
            usb = kit.OpenUsbDeviceSessionByKey(device.DeviceKey, device.SourceApiKind);
            if (usb is null || !new RkdlFactory().TryCreate(usb, out var protocol) ||
                protocol is not RkdlSession created)
            {
                return null;
            }

            session = created;
            // READ_CHIP_INFO is a loader-mode command; a maskrom device answers with a failure and
            // the identity keeps the maskrom label, exactly like RKDevTool's MASKROM rows.
            // <para>READ_CHIP_INFO 是 loader 模式命令；maskrom 设备会应答失败，身份保持 maskrom
            // 标签，与 RKDevTool 的 MASKROM 行一致。</para>
            var label = session.IsRockusbMode ? ProbeChipName(session) : "maskrom";
            return $"{label}-{location}";
        }
        catch (Exception ex)
        {
            ReportProbeFailure(device, ex, failureSink);
            return null;
        }
        finally
        {
            session?.Dispose();
            if (session is null)
            {
                usb?.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads the chip name from a rockusb-mode session; an empty name keeps the loader label so the
    /// row still reads as a loader port.
    /// <para>从 rockusb 模式会话读取芯片名；名字为空时保持 loader 标签，使该行仍可读作 loader 端口。</para>
    /// </summary>
    /// <param name="session">The rockusb session to query. <para>待查询的 rockusb 会话。</para></param>
    /// <returns>The chip name, or <c>loader</c> when the device reported none.
    /// <para>芯片名；设备未报告时为 <c>loader</c>。</para></returns>
    private static string ProbeChipName(RkdlSession session)
    {
        var chip = session.ReadChipInfo().ChipName;
        return chip.Length == 0 ? "loader" : chip;
    }

    /// <summary>
    /// Formats the observed location evidence as the row suffix: on Windows the RKDevTool-style
    /// physical hub chain (for example <c>1-3-1</c>), falling back to the PnP instance-id tail, and
    /// elsewhere the backend's physical port path verbatim.
    /// <para>把已观测的位置证据格式化为行后缀：Windows 上为 RKDevTool 风格的物理集线器链
    /// （如 <c>1-3-1</c>），失败时回退 PnP 实例 ID 尾部；其他后端为后端的物理端口路径原样。</para>
    /// </summary>
    /// <param name="device">The discovered USB device descriptor. <para>已发现的 USB 设备描述符。</para></param>
    /// <returns>The formatted location text, or <c>null</c> when neither evidence source reported one.
    /// <para>格式化后的位置文本；两个证据源都未报告时返回 <c>null</c>。</para></returns>
    private static string? FormatLocation(UsbDeviceInfo device)
    {
        if (device.DeviceInstanceId is { Length: > 0 } instanceId)
        {
            return UsbLocationChain.TryRead(instanceId) ?? InstanceLocationTail(instanceId);
        }

        return device.PhysicalPortPath is { Length: > 0 } port && !port.Any(char.IsControl)
            ? port
            : null;
    }

    /// <summary>
    /// Extracts the hub-and-port tail from a PnP device instance id such as
    /// <c>USB\VID_2207&amp;PID_350B&amp;MI_00\6&amp;2F0A3B7&amp;0&amp;2&amp;0000</c>: the trailing
    /// four-digit interface qualifier is dropped so the parent and every interface child of one
    /// port share the suffix <c>6&amp;2F0A3B7&amp;0&amp;2</c>.
    /// <para>从形如 <c>USB\VID_2207&amp;PID_350B&amp;MI_00\6&amp;2F0A3B7&amp;0&amp;2&amp;0000</c> 的
    /// PnP 设备实例 ID 中提取集线器与端口尾部：去掉末尾的四位接口限定段，使父节点与同一端口的各
    /// 接口子节点共享后缀 <c>6&amp;2F0A3B7&amp;0&amp;2</c>。</para>
    /// </summary>
    /// <param name="instanceId">The PnP device instance id. <para>PnP 设备实例 ID。</para></param>
    /// <returns>The location tail, or <c>null</c> when the id carries no device-instance tail.
    /// <para>位置尾部；实例 ID 没有设备实例尾部时返回 <c>null</c>。</para></returns>
    internal static string? InstanceLocationTail(string instanceId)
    {
        var separator = instanceId.LastIndexOf('\\');
        if (separator < 0 || separator == instanceId.Length - 1)
        {
            return null;
        }

        var tail = instanceId[(separator + 1)..];
        // An id without a device-instance tail (USB\VID_…&amp;PID_…) exposes no location evidence.
        // <para>没有设备实例尾部的 ID（USB\VID_…&amp;PID_…）不提供任何位置证据。</para>
        if (tail.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = tail.Split('&');
        // A hardware instance id has at least two &-segments (hub instance and port). An interface
        // child appends one more qualifier such as 0000; port numbers are never four padded hex
        // digits, so only such a trailing qualifier is dropped.
        // <para>硬件实例 ID 至少有两段（集线器实例与端口）。接口子节点会多出一段如 0000 的限定段；
        // 端口号绝不会是四位补零十六进制，因此只去掉这样的末尾限定段。</para>
        if (segments.Length < 2)
        {
            return null;
        }

        return segments.Length >= 3 && IsInterfaceQualifier(segments[^1])
            ? string.Join('&', segments.AsSpan(0, segments.Length - 1))
            : tail;
    }

    /// <summary>
    /// Recognizes the four-hex-digit interface qualifier that Windows appends to interface child
    /// instance ids, such as <c>0000</c> for <c>MI_00</c>.
    /// <para>识别 Windows 附加在接口子实例 ID 末尾的四位十六进制接口限定段，如 <c>MI_00</c> 对应的 <c>0000</c>。</para>
    /// </summary>
    private static bool IsInterfaceQualifier(string segment) =>
        segment.Length == 4 && segment.All(char.IsAsciiHexDigit);

    /// <summary>
    /// Turns a probe failure that collapsed into a <c>null</c> return into a diagnosable error line.
    /// <para>把已折叠为 <c>null</c> 返回值的探测失败转成可诊断的错误行。</para>
    /// </summary>
    /// <param name="device">The device whose probe failed. <para>探测失败的设备。</para></param>
    /// <param name="ex">The exception that was swallowed by the best-effort contract.
    /// <para>被尽力而为契约吞掉的异常。</para></param>
    /// <param name="failureSink">Optional verbatim diagnostics sink; see <see cref="Read(ICommKit,UsbDeviceInfo,IUsbLogger?)"/>.
    /// <para>可选的逐字诊断汇；参见 <see cref="Read(ICommKit,UsbDeviceInfo,IUsbLogger?)"/>.</para></param>
    private static void ReportProbeFailure(UsbDeviceInfo device, Exception ex, IUsbLogger? failureSink)
    {
        var message = $"RKDL chip-info probe failed for device {device.DeviceKey} (api {device.SourceApiKind}): {ex.GetType().Name}: {ex.Message}";
        if (failureSink is { } sink)
        {
            sink.Log($"[ERROR] {message}");
            return;
        }

        UsbTrace.LogError(message);
    }
}