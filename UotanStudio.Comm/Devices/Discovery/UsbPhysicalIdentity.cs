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
    /// <param name="info">
    /// The enumerated USB device. <para>已枚举的 USB 设备。</para>
    /// </param>
    /// <param name="serial">
    /// Serial used when no container id is available. <para>无容器 ID 时使用的序列号。</para>
    /// </param>
    /// <returns>The container-id identity, else a serial-based identity, or <see langword="null"/>.
    /// <para>容器 ID 标识；否则为基于序列号的标识；均不可用时返回 <see langword="null"/>。</para></returns>
    public static string? Of(UsbDeviceInfo info, string? serial) =>
        info.ContainerId is { } container && container != Guid.Empty
            ? $"usb-container:{container:N}"
            : FromSerial(info.VendorId, info.ProductId, serial);

    /// <summary>Serial-based identity; rejects absent, oversized, or control-character-bearing serials.
    /// <para>基于序列号的标识；拒绝缺失、超长或含控制字符的序列号。</para></summary>
    /// <param name="vendorId">USB vendor id formatted as four hex digits.
    /// <para>按四位十六进制格式化的 USB 厂商 ID。</para></param>
    /// <param name="productId">USB product id formatted as four hex digits.
    /// <para>按四位十六进制格式化的 USB 产品 ID。</para></param>
    /// <param name="serial">Candidate serial; rejected when blank, equal to <c>UNKNOWN</c>, longer than 128 characters, or containing a control character.
    /// <para>候选序列号；为空、等于 <c>UNKNOWN</c>、超过 128 个字符或含控制字符时被拒绝。</para></param>
    /// <returns><c>usb:VVVV:PPPP:serial</c>, or <see langword="null"/> when the serial is rejected.
    /// <para>形如 <c>usb:VVVV:PPPP:serial</c> 的标识；序列号被拒绝时返回 <see langword="null"/>。</para></returns>
    public static string? FromSerial(ushort vendorId, ushort productId, string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial == "UNKNOWN" ||
            serial.Length > 128 || serial.Any(char.IsControl))
        {
            return null;
        }

        return $"usb:{vendorId:X4}:{productId:X4}:{serial}";
    }

    /// <summary>Resolves the single physical identity a server serial maps to across the observed USB nodes, matching by exact serial, a PnP instance-id tail, or a case-only serial difference.
    /// <para>在观测到的 USB 节点中解析 server 序列号映射到的唯一物理标识，按精确序列号、 PnP 实例 ID 尾部或仅大小写差异匹配。</para></summary>
    /// <param name="infos">The observed USB nodes.<para>观测到的 USB 节点集合。</para></param>
    /// <param name="serial">The server-reported serial to bind.<para>待绑定的 server 上报序列号。</para></param>
    /// <param name="protocol">Protocol name a candidate node must classify as.
    /// <para>候选节点必须匹配的分类协议名。</para></param>
    /// <returns>The unique physical identity, or <see langword="null"/> when zero or several nodes match.
    /// <para>唯一的物理标识；匹配零个或多个节点时返回 <see langword="null"/>。</para></returns>
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