using UotanStudio.Comm.Transports.Usb.Backend.Windows;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Derives the RKDevTool-style physical hub chain, such as <c>1-3-1</c>, of an enumerated USB device
/// on Windows from the PnP <c>DEVPKEY_Device_LocationPaths</c> property: the segment numbers after
/// <c>USBROOT</c> name the root-hub port and every downstream hub port on the way to the device.
/// Other platforms keep their backend-reported port path, so this helper is Windows-only.
/// <para>在 Windows 上从 PnP <c>DEVPKEY_Device_LocationPaths</c> 属性推导 RKDevTool 风格的物理集线器
/// 链（如 <c>1-3-1</c>）：<c>USBROOT</c> 之后的各段编号依次是根集线器端口与通往设备的各级下游集线器
/// 端口。其他平台保留后端上报的端口路径，因此该辅助仅用于 Windows。</para>
/// </summary>
internal static class UsbLocationChain
{
    /// <summary>
    /// Reads the physical hub chain of one device instance id, for example <c>1-3-1</c> when the
    /// device hangs on port 1 of a hub that hangs on port 3 of the root hub's port 1.
    /// <para>读取一个设备实例标识的物理集线器链，例如设备挂在「根集线器端口 1 下的集线器端口 3 下的
    /// 端口 1」时为 <c>1-3-1</c>。</para>
    /// </summary>
    /// <param name="instanceId">The PnP device instance id. <para>PnP 设备实例标识。</para></param>
    /// <returns>
    /// The dash-joined port chain, or <c>null</c> when the platform is not Windows, the devnode is
    /// absent, no location path was reported, or no root-anchored USB chain could be parsed.
    /// <para>以连字符连接的端口链；平台非 Windows、设备节点不存在、未上报位置路径或解析不出以根为
    /// 锚的 USB 链时返回 <c>null</c>。</para>
    /// </returns>
    internal static string? TryRead(string instanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        try
        {
            return ReadChain(instanceId);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Native-backed chain read, separated for the platform gate. <para>平台门之后的原生链读取。</para></summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string? ReadChain(string instanceId)
    {
        if (!UsbPnpNative.TryLocateDevNode(instanceId, out var devInst))
        {
            return null;
        }

        var locationPaths = UsbPnpNative.ReadProperties(devInst, instanceId).LocationPaths;
        foreach (var path in locationPaths)
        {
            if (ParseRootChain(path) is { } chain)
            {
                return chain;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the root-anchored USB segment chain from one location path. The PCIROOT form carries
    /// the physical topology; the ACPI form is the same hardware under another naming, so only the
    /// <c>USBROOT</c> variant is parsed.
    /// <para>从一条位置路径解析以根为锚的 USB 段链。PCIROOT 形式承载物理拓扑；ACPI 形式只是同一硬件
    /// 的另一命名，因此只解析 <c>USBROOT</c> 变体。</para>
    /// </summary>
    internal static string? ParseRootChain(string? locationPath)
    {
        if (string.IsNullOrEmpty(locationPath))
        {
            return null;
        }

        var rootIndex = locationPath.IndexOf("USBROOT(", StringComparison.OrdinalIgnoreCase);
        if (rootIndex < 0)
        {
            return null;
        }

        var segments = locationPath[(rootIndex + "USBROOT(".Length)..].Split('#');
        List<string> ports = [];
        foreach (var segment in segments)
        {
            // USB(n) names one port on the way to the device; USBMI(n) is an interface child and
            // never part of the physical chain; the first segment is the root-hub remainder ("0)")
            // and carries no port number.
            // <para>USB(n) 是通往设备路上的一个端口；USBMI(n) 是接口子节点，不属于物理链；首段是
            // 根集线器的剩余部分（"0)"），不携带端口号。</para>
            if (segment.StartsWith("USB(", StringComparison.OrdinalIgnoreCase))
            {
                var number = segment[4..segment.IndexOf(')')];
                if (number.Length is 0 or > 7 || !number.All(char.IsAsciiDigit))
                {
                    return null;
                }

                ports.Add(number);
            }
            else if (!segment.StartsWith("USBMI(", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }

        return ports.Count > 0 ? string.Join('-', ports) : null;
    }
}