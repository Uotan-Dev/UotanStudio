namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Identifies one USB device a hotplug event touched so an incremental pass can re-probe only that device
/// instead of walking the whole bus. A hint carries every identity form the backends use; a backend matches
/// on the forms it published at discovery and ignores the rest.
/// <para>标识热插拔事件触及的单台 USB 设备，使增量轮次只重新探测该设备而不必遍历整条总线。
/// 提示携带各后端使用的全部身份形式；后端按发现时公布的形式匹配，其余忽略。</para>
/// </summary>
internal sealed class UsbDeviceProbeHint
{
    /// <summary>Gets the change kind the event source reported. <para>获取事件来源上报的变化类型。</para></summary>
    public UsbDeviceChangeKind Kind { get; init; }

    /// <summary>
    /// Gets the OS device path (a WinUSB interface path, Linux <c>/dev/bus/usb/BBB/DDD</c>, or the
    /// libusb <c>Bus X Device Y</c> form), when the source reports one.
    /// <para>获取操作系统设备路径（WinUSB 接口路径、Linux 的 <c>/dev/bus/usb/BBB/DDD</c>，或 libusb 的
    /// <c>Bus X Device Y</c> 形式）；来源上报时才有值。</para>
    /// </summary>
    public string? DevicePath { get; init; }

    /// <summary>Gets the Windows PnP instance id when the source reports one.
    /// <para>获取来源上报的 Windows PnP 实例 ID（若有）。</para></summary>
    public string? InstanceId { get; init; }

    /// <summary>
    /// Gets the backend-scoped physical port identity when the event source reports one.
    /// <para>获取事件来源上报的后端范围内物理端口标识（若有）。</para>
    /// </summary>
    public string? PhysicalPortPath { get; init; }

    /// <summary>Gets the vendor id when the event carries one. <para>获取事件携带的厂商 ID（若有）。</para></summary>
    public ushort? VendorId { get; init; }

    /// <summary>Gets the product id when the event carries one. <para>获取事件携带的产品 ID（若有）。</para></summary>
    public ushort? ProductId { get; init; }

    /// <summary>Gets whether this hint denotes a device that left the bus, so it only removes a cached entry.
    /// <para>报告该提示是否指名一台已离开总线的设备；这类提示只移除缓存条目。</para></summary>
    public bool IsRemoval => Kind == UsbDeviceChangeKind.Removed;

    /// <summary>
    /// Gets whether the hint is specific enough to select at most one device. A hint with no path and no
    /// VID/PID pair cannot be served incrementally and forces a full pass.
    /// <para>报告提示是否足够具体、至多选中一台设备。既无路径也无 VID/PID 对的提示无法增量处理，
    /// 必须回退到全量轮次。</para>
    /// </summary>
    public bool IsSpecific =>
        !string.IsNullOrWhiteSpace(DevicePath) ||
        !string.IsNullOrWhiteSpace(InstanceId) ||
        !string.IsNullOrWhiteSpace(PhysicalPortPath) ||
        (VendorId is not null && ProductId is not null);

    /// <summary>
    /// Matches the hint against a cached device using only the identity forms the hint actually carries.
    /// An absent field never rejects a match; a present field must agree exactly. VID and PID are evaluated
    /// together so a partially populated pair cannot silently widen a match.
    /// <para>只用提示实际携带的身份形式匹配缓存设备。缺失字段永不否决匹配；存在字段必须精确一致。
    /// VID 与 PID 一起判定，避免只填一半时悄然放宽匹配。</para>
    /// </summary>
    /// <param name="device">The cached device to test. <para>待匹配的缓存设备。</para></param>
    /// <returns><see langword="true"/> when the hint can denote this device.
    /// <para>该提示可能指向这台设备时为 <see langword="true"/>。</para></returns>
    public bool Matches(UsbDeviceInfo device)
    {
        if (VendorId is not null && ProductId is not null &&
            (device.VendorId != VendorId.Value || device.ProductId != ProductId.Value))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(DevicePath) &&
            !string.Equals(DevicePath, device.DevicePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(InstanceId) &&
            !string.Equals(InstanceId, device.DeviceInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(PhysicalPortPath) &&
            !string.Equals(PhysicalPortPath, device.PhysicalPortPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}