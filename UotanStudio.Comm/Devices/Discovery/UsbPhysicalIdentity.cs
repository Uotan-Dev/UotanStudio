using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Derives the verified physical identity that binds a server target to exactly one USB node.
/// <para>推导把某个 server 目标唯一绑定到一个 USB 节点的、经过验证的物理标识。</para>
/// Ambiguous or missing evidence yields <c>null</c>; the identity is never guessed from a lone match count.
/// <para>证据多解或缺失一律返回 <c>null</c>；绝不凭单一计数猜测身份。</para>
/// </summary>
public static class UsbPhysicalIdentity
{
    /// <summary>
    /// Prefers the USB container id (stable across interface renumbering); otherwise a VID/PID/serial tuple.
    /// <para>优先使用 USB 容器 ID（跨接口重编号稳定）；否则使用 VID/PID/序列号三元组。</para>
    /// </summary>
    public static string? Of(UsbDeviceInfo info, string? serial) =>
        info.ContainerId is { } container && container != Guid.Empty
            ? $"usb-container:{container:N}"
            : FromSerial(info.VendorId, info.ProductId, serial);

    /// <summary>Serial-based identity; rejects absent, oversized, or control-character-bearing serials.
    /// 基于序列号的标识；拒绝缺失、超长或含控制字符的序列号。</summary>
    public static string? FromSerial(ushort vendorId, ushort productId, string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial == "UNKNOWN" ||
            serial.Length > 128 || serial.Any(char.IsControl))
        {
            return null;
        }

        return $"usb:{vendorId:X4}:{productId:X4}:{serial}";
    }

    /// <summary>
    /// Resolves the single physical identity that a server serial maps to across the observed USB nodes,
    /// matching by exact serial, a PnP instance-id tail, or a case-only serial difference (some
    /// devices report different casing over the protocol than in the descriptor); returns
    /// <c>null</c> unless exactly one candidate remains, so case folding can never merge two devices.
    /// <para>在观测到的 USB 节点中解析某个 server 序列号映射到的唯一物理标识，按精确序列号、
    /// PnP 实例 ID 尾部或仅大小写差异匹配（部分设备经协议上报与描述符中的大小写不同）；
    /// 仅当恰好剩一个候选时返回，否则返回 <c>null</c>，因此大小写折叠绝不可能合并两台设备。</para>
    /// </summary>
    public static string? ServerPhysicalId(IReadOnlyList<UsbDeviceInfo> infos, string serial, string protocol)
    {
        var ids = infos
            .Where(info => UsbProtocolClassifier.Classify(info) == protocol &&
                (string.Equals(info.SerialNumber, serial, StringComparison.Ordinal) ||
                    info.ContainerId.HasValue && InstanceIdMatchesSerial(info.DeviceInstanceId, serial) ||
                    SerialDiffersOnlyByCase(info.SerialNumber, serial)))
            .Select(info => Of(info, info.SerialNumber))
            .Where(id => id is not null)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    private static bool SerialDiffersOnlyByCase(string? candidate, string serial)
        => !string.IsNullOrEmpty(candidate) && candidate != "UNKNOWN" &&
            string.Equals(candidate, serial, StringComparison.OrdinalIgnoreCase);

    private static bool InstanceIdMatchesSerial(string? instanceId, string serial)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        var separator = instanceId.LastIndexOf('\\');
        return separator >= 0 && string.Equals(instanceId[(separator + 1)..], serial,
            StringComparison.OrdinalIgnoreCase);
    }
}