using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Maps a USB device to its download or debug-bridge protocol from VID/PID bootrom signatures and
/// interface descriptor triples.
/// <para>依据 VID/PID 的 bootrom 特征与接口描述符三元组，把 USB 设备映射到其下载或调试桥协议。</para>
/// </summary>
public static class UsbProtocolClassifier
{
    private const ushort QualcommVid = 0x05C6;
    private const ushort MediaTekVid = 0x0E8D;
    private const ushort SonyVid = 0x0FCE;
    private const ushort SierraVid = 0x1199;
    private const ushort NetgearVid = 0x0846;
    private const ushort ZteVid = 0x19D2;

    /// <summary>
    /// Classifies the device as <c>edl</c>, <c>mtk</c> or <c>rkdl</c> from its bootrom VID/PID pair,
    /// otherwise from the first interface matching a known debug-bridge triple.
    /// <para>先按 bootrom 的 VID/PID 对把设备分类为 <c>edl</c>、<c>mtk</c> 或 <c>rkdl</c>，否则按首个匹配已知调试桥三元组的接口分类。</para>
    /// </summary>
    /// <param name="device">
    /// The enumerated USB device. <para>已枚举的 USB 设备。</para>
    /// </param>
    /// <returns>The protocol name, or <see langword="null"/> when nothing identifies a known protocol.
    /// <para>协议名；没有任何已知协议特征时返回 <see langword="null"/>。</para></returns>
    public static string? Classify(UsbDeviceInfo device)
    {
        var bootrom = ClassifyBootrom(device.VendorId, device.ProductId);
        if (bootrom is not null)
            return bootrom;

        foreach (var iface in device.Interfaces)
        {
            var protocol = ClassifyInterface(iface.Class, iface.SubClass, iface.Protocol);
            if (protocol is not null) return protocol;
        }
        return device.InterfaceMetadataObserved
            ? ClassifyInterface(device.InterfaceClass, device.InterfaceSubClass, device.InterfaceProtocol)
            : null;
    }

    /// <summary>Classifies from an explicit VID/PID pair and one interface triple, using the same rules as <see cref="Classify(UsbDeviceInfo)"/>, so topology nodes and endpoint inventory never disagree.
    /// <para>按显式 VID/PID 与单个接口三元组分类，规则与 <see cref="Classify(UsbDeviceInfo)"/> 完全一致， 因此拓扑节点与端点清单的结论永不分歧。</para></summary>
    /// <param name="vendorId">USB vendor id.<para>USB 厂商 ID。</para></param>
    /// <param name="productId">USB product id, or null when the node carries none.
    /// <para>USB 产品 ID；节点未携带时为 null。</para></param>
    /// <param name="interfaceClass">bInterfaceClass, or null when unknown.
    /// <para>bInterfaceClass；未知时为 null。</para></param>
    /// <param name="interfaceSubClass">bInterfaceSubClass, or null when unknown.
    /// <para>bInterfaceSubClass；未知时为 null。</para></param>
    /// <param name="interfaceProtocol">bInterfaceProtocol, or null when unknown.
    /// <para>bInterfaceProtocol；未知时为 null。</para></param>
    /// <returns>The protocol name, or <see langword="null"/> when nothing identifies a known protocol.
    /// <para>协议名；没有任何已知协议特征时返回 <see langword="null"/>。</para></returns>
    public static string? Classify(ushort vendorId, ushort? productId, byte? interfaceClass,
        byte? interfaceSubClass, byte? interfaceProtocol)
        => ClassifyBootrom(vendorId, productId) ?? ClassifyInterface(interfaceClass, interfaceSubClass,
            interfaceProtocol);

    /// <summary>Maps one interface triple to a debug-bridge protocol name.
    /// <para>把单个接口三元组映射到调试桥协议名。</para></summary>
    /// <param name="interfaceClass">bInterfaceClass.<para>bInterfaceClass。</para></param>
    /// <param name="interfaceSubClass">bInterfaceSubClass.<para>bInterfaceSubClass。</para></param>
    /// <param name="interfaceProtocol">bInterfaceProtocol.<para>bInterfaceProtocol。</para></param>
    /// <returns><c>hdc</c>, <c>adb</c> or <c>fastboot</c>, or <see langword="null"/> for any other triple.
    /// <para><c>hdc</c>、<c>adb</c> 或 <c>fastboot</c>；其它三元组返回 <see langword="null"/>。</para></returns>
    public static string? ClassifyInterface(byte? interfaceClass, byte? interfaceSubClass,
        byte? interfaceProtocol) =>
        (interfaceClass, interfaceSubClass, interfaceProtocol) switch
        {
            (0xff, 0x50, 1) => "hdc",
            (0xff, 0x42, 1) => "adb",
            (0xff, 0x42, 3) => "fastboot",
            _ => null
        };

    /// <summary>
    /// Maps a bootrom VID/PID pair to <c>edl</c>, <c>mtk</c> or <c>rkdl</c>, covering Qualcomm and
    /// MediaTek bootrom and download ports plus Rockchip loader ports.
    /// <para>把 bootrom 的 VID/PID 对映射到 <c>edl</c>、<c>mtk</c> 或 <c>rkdl</c>，覆盖高通与联发科的 bootrom/下载端口以及瑞芯微 loader 端口。</para>
    /// </summary>
    /// <param name="vendorId">
    /// USB vendor id. <para>USB 厂商 ID。</para>
    /// </param>
    /// <param name="productId">
    /// USB product id, or null when the node carries none. <para>USB 产品 ID；节点未携带时为 null。</para>
    /// </param>
    /// <returns>
    /// The bootrom protocol name, or <see langword="null"/> when the pair is not in any of the tables.
    /// <para>bootrom 协议名；该组合不在任何表内时返回 <see langword="null"/>。</para>
    /// </returns>
    private static string? ClassifyBootrom(ushort vendorId, ushort? productId)
    {
        var product = productId ?? 0;
        if (vendorId == QualcommVid &&
            product is 0x9006 or 0x9008 or 0x900E or 0x901D or 0x9025 or 0x9307)
        {
            return "edl";
        }

        if (vendorId == SonyVid && product is 0x9DDE or 0xADE3 or 0xADE5 or 0xADED)
        {
            return "edl";
        }

        if ((vendorId == SierraVid && product is 0x9062 or 0x9070 or 0x9090)
            || (vendorId == NetgearVid && product == 0x68E0)
            || (vendorId == ZteVid && product == 0x0076))
        {
            return "edl";
        }

        if (MtkFactory.IsMtkBootromPort(vendorId, product))
        {
            return "mtk";
        }

        if (vendorId == RkdlConstants.RockchipVid &&
            ((product >> 8) != 0 || RkdlConstants.KnownLoaderProductIds.Contains(product)))
        {
            return "rkdl";
        }

        return null;
    }
}