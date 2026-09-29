using System.Globalization;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>
/// <para>活动配置里一个接口描述符的纯数据形状：接口号与类/子类/协议三元组。它不引用任何
/// LibUsbDotNet 类型，因此 <see cref="LibUsbTopologyGraph"/> 可以在没有原生 libusb 运行时的机器上
/// （含测试宿主）被完整验证。</para>
/// The plain data shape of one interface descriptor from the active configuration: the interface number
/// plus the class/subclass/protocol triple. It references no LibUsbDotNet type, so
/// <see cref="LibUsbTopologyGraph"/> can be verified completely on machines without a native libusb
/// runtime (including the test host).
/// </summary>
internal sealed record LibUsbTopologyInterface
{
    /// <summary>The bInterfaceNumber of the interface. <para>接口的 <c>bInterfaceNumber</c>。</para></summary>
    /// <value>The interface number as the descriptor reports it. <para>描述符报告的接口号。</para></value>
    internal required byte Number { get; init; }

    /// <summary>The bInterfaceClass of the interface. <para>接口的 <c>bInterfaceClass</c>。</para></summary>
    /// <value>The raw class code. <para>原始类代码。</para></value>
    internal byte Class { get; init; }

    /// <summary>The bInterfaceSubClass of the interface. <para>接口的 <c>bInterfaceSubClass</c>。</para></summary>
    /// <value>The raw subclass code. <para>原始子类代码。</para></value>
    internal byte SubClass { get; init; }

    /// <summary>The bInterfaceProtocol of the interface. <para>接口的 <c>bInterfaceProtocol</c>。</para></summary>
    /// <value>The raw protocol code. <para>原始协议代码。</para></value>
    internal byte Protocol { get; init; }
}

/// <summary>
/// <para>libusb 设备清单里一个设备的纯数据形状：总线号、端口链、VID/PID/bcdDevice、序列号、协商速度与
/// 活动配置的接口列表。字段只承载后端读到的原始值；厂商/产品/版本回退解析、接口三元组回退与协议分类都由
/// <see cref="UsbTopologyNodes.Create"/> 统一补齐。</para>
/// The plain data shape of one device from the libusb device list: bus number, port chain, VID/PID/
/// bcdDevice, serial, negotiated speed and the interface list of the active configuration. Fields carry
/// only raw values the backend read; fallback identity and triple parsing and protocol classification are
/// unified by <see cref="UsbTopologyNodes.Create"/>.</summary>
internal sealed record LibUsbTopologyDevice
{
    /// <summary>The libusb bus number the device sits on. <para>设备所在的 libusb 总线号。</para></summary>
    /// <value>The raw bus number. <para>原始总线号。</para></value>
    internal required byte BusNumber { get; init; }

    /// <summary>The root-to-device port chain, outermost port first. <para>从根到设备的端口链，外层端口在前。</para></summary>
    /// <value>The port numbers as <c>libusb_get_port_numbers</c> reports them; an empty chain degrades to
    /// a bus-root-parented fallback node. <para><c>libusb_get_port_numbers</c> 报告的端口号；空链降级为挂在总线根下的回退节点。</para></value>
    internal IReadOnlyList<byte> PortChain { get; init; } = [];

    /// <summary>The device descriptor vendor id. <para>设备描述符的厂商 ID。</para></summary>
    /// <value>The raw vendor id. <para>原始厂商 ID。</para></value>
    internal required ushort VendorId { get; init; }

    /// <summary>The device descriptor product id. <para>设备描述符的产品 ID。</para></summary>
    /// <value>The raw product id. <para>原始产品 ID。</para></value>
    internal required ushort ProductId { get; init; }

    /// <summary>The device descriptor release (bcdDevice). <para>设备描述符的版本号（bcdDevice）。</para></summary>
    /// <value>The raw release, or <see langword="null"/> when the descriptor could not be read. <para>原始版本；描述符读不到时为 <see langword="null"/>。</para></value>
    internal ushort? BcdDevice { get; init; }

    /// <summary>The serial number string when the backend already has it. <para>后端已持有时的序列号文本。</para></summary>
    /// <value>The serial text, or <see langword="null"/>; reading it from a closed libusb device is not
    /// possible, so the native provider leaves it <see langword="null"/>.
    /// <para>序列号文本或 <see langword="null"/>；未打开的 libusb 设备无法读取字符串描述符，因此原生提供程序保持 <see langword="null"/>。</para></value>
    internal string? Serial { get; init; }

    /// <summary>The negotiated link speed. <para>协商出的链路速度。</para></summary>
    /// <value>The mapped speed, <see cref="UsbDeviceSpeed.Unknown"/> when unreported. <para>映射后的速度；未报告时为 <see cref="UsbDeviceSpeed.Unknown"/>。</para></value>
    internal UsbDeviceSpeed Speed { get; init; } = UsbDeviceSpeed.Unknown;

    /// <summary>The interfaces of the active configuration. <para>活动配置的接口列表。</para></summary>
    /// <value>The interface list; empty when the active configuration could not be inspected. <para>接口列表；无法查看活动配置时为空。</para></value>
    internal IReadOnlyList<LibUsbTopologyInterface> Interfaces { get; init; } = [];
}

/// <summary>
/// <para>把 libusb 的总线号与端口链重建为 总线根 → 集线器 → 设备 → 接口 的扁平节点集合，是 libusb 后端的
/// 纯塑形层：输入为 <see cref="LibUsbTopologyDevice"/> 记录列表，不触碰原生库。设备节点以
/// <see cref="LibUsbPortIdentity"/> 的规范令牌 <c>libusb-port:&lt;bus&gt;:&lt;chain&gt;</c> 为标识，父标识为
/// 去掉最后一个端口的链；链只剩一个端口时父为总线根节点 <c>libusb-bus:&lt;n&gt;</c>。当端口链隐含了中间集线器
/// 层级但清单中没有匹配设备时，会补发一个 <see cref="UsbTopologyNodeKind.Hub"/> 节点且
/// <c>Description</c> 为 <see langword="null"/>——该节点是根据链推断出来的，并非实际观测到的设备。端口链无法
/// 构成规范令牌（空链、含 0 端口或超过 7 级）时，设备改用 <c>libusb-device:&lt;bus&gt;:&lt;vid&gt;:&lt;pid&gt;</c>
/// 回退标识并挂在总线根下；令牌相同的重复记录折叠到首次出现。观测到的设备若其端口链
/// 是另一设备链的严格前缀，则按拓扑角色标注为集线器。每个节点只声明 <c>ParentInstanceId</c>，嵌套由共享的
/// <see cref="UsbTopologyTree"/> 完成。</para>
/// The pure shaping layer that rebuilds libusb bus numbers and port chains into flat
/// bus-root → hub → device → interface nodes. It consumes <see cref="LibUsbTopologyDevice"/> records and
/// never touches the native library. Device nodes are keyed by the canonical
/// <see cref="LibUsbPortIdentity"/> token <c>libusb-port:&lt;bus&gt;:&lt;chain&gt;</c> and parented to the
/// chain with its last port removed; when only one port remains the parent is the bus root node
/// <c>libusb-bus:&lt;n&gt;</c>. When a chain implies an intermediate hub level but no listed device matches
/// it, an inferred <see cref="UsbTopologyNodeKind.Hub"/> node is emitted with <c>Description</c>
/// <see langword="null"/> — the node is inferred from the chain, not observed. A chain that cannot form a
/// canonical token (empty, a zero port, or deeper than seven levels) degrades the device to a fallback
/// <c>libusb-device:&lt;bus&gt;:&lt;vid&gt;:&lt;pid&gt;</c> identity parented to its bus root; records that
/// map to the same token collapse to the first occurrence. An
/// observed device whose chain is a strict prefix of another device's chain is stated as a hub. Every
/// node only declares <c>ParentInstanceId</c>; nesting is done by the shared <see cref="UsbTopologyTree"/>.
/// </summary>
internal static class LibUsbTopologyGraph
{
    /// <summary>
    /// Builds the stable instance id of the synthetic bus root for one bus number.
    /// <para>构造某一总线号的合成总线根节点的稳定实例标识。</para>
    /// </summary>
    /// <param name="busNumber">
    /// The libusb bus number. <para>libusb 总线号。</para>
    /// </param>
    /// <returns>
    /// The <c>libusb-bus:&lt;n&gt;</c> identifier. <para><c>libusb-bus:&lt;n&gt;</c> 标识。</para>
    /// </returns>
    internal static string BusRootInstanceId(byte busNumber)
        => "libusb-bus:" + busNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the instance id of a function node below the given device token.
    /// <para>由设备令牌构造其下方功能节点的实例标识。</para>
    /// </summary>
    /// <param name="deviceToken">
    /// The canonical or fallback device token. <para>规范或回退的设备令牌。</para>
    /// </param>
    /// <param name="interfaceNumber">
    /// The interface number. <para>接口号。</para>
    /// </param>
    /// <returns>
    /// The device token extended with <c>&amp;MI_</c> and a two-digit hex number.
    /// <para>设备令牌后接 <c>&amp;MI_</c> 与两位十六进制数。</para>
    /// </returns>
    internal static string InterfaceInstanceId(string deviceToken, byte interfaceNumber)
        => $"{deviceToken}&MI_{interfaceNumber:X2}";

    /// <summary>
    /// Shapes the device records into flat topology nodes: one bus root per distinct bus number, inferred
    /// hub nodes for missing chain levels, one node per device and one function node per active-config
    /// interface. Duplicate device records (same bus, chain and identity) collapse to their first
    /// occurrence.
    /// <para>把设备记录塑形为扁平拓扑节点：每个不同总线号一个总线根，缺失链层级补发推断集线器节点，
    /// 每个设备一个节点，活动配置每个接口一个功能节点。重复设备记录（同总线、同链、同标识）折叠到首次出现。</para>
    /// </summary>
    /// <param name="devices">
    /// The plain device records in enumeration order. <para>按枚举顺序给出的纯设备记录。</para>
    /// </param>
    /// <returns>
    /// Flat nodes with only <c>ParentInstanceId</c> links, ready for <see cref="UsbTopologyTree.Build"/>.
    /// <para>仅携带 <c>ParentInstanceId</c> 链接的扁平节点，可直接交给 <see cref="UsbTopologyTree.Build"/>。</para>
    /// </returns>
    internal static IReadOnlyList<UsbTopologyNode> Build(IReadOnlyList<LibUsbTopologyDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var tokens = AssignTokens(devices);
        var observedTokens = new HashSet<string>(tokens, StringComparer.Ordinal);
        var hostingTokens = FindHostingTokens(devices, tokens);
        var nodes = new List<UsbTopologyNode>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var rootedBuses = new HashSet<byte>();

        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var token = tokens[index];
            if (!emitted.Add(token))
            {
                continue;
            }

            if (rootedBuses.Add(device.BusNumber))
            {
                nodes.Add(BusRootNode(device.BusNumber));
            }

            var parentInstanceId = BusRootInstanceId(device.BusNumber);
            if (LibUsbPortIdentity.Format(device.BusNumber, device.PortChain) is not null)
            {
                for (var length = 1; length < device.PortChain.Count; length++)
                {
                    var prefixToken = LibUsbPortIdentity.Format(device.BusNumber,
                        device.PortChain.Take(length));
                    if (prefixToken is null)
                    {
                        break;
                    }

                    if (!observedTokens.Contains(prefixToken) && emitted.Add(prefixToken))
                    {
                        nodes.Add(InferredHubNode(prefixToken, parentInstanceId));
                    }

                    parentInstanceId = prefixToken;
                }
            }

            nodes.Add(DeviceNode(device, token, parentInstanceId,
                hostingTokens.Contains(token)
                    ? UsbTopologyNodeKind.Hub
                    : UsbTopologyNodeKind.Device));

            foreach (var iface in device.Interfaces)
            {
                var interfaceId = InterfaceInstanceId(token, iface.Number);
                if (emitted.Add(interfaceId))
                {
                    nodes.Add(InterfaceNode(device, interfaceId, token, iface));
                }
            }
        }

        return nodes;
    }

    /// <summary>
    /// Assigns a token to every device record: the canonical <see cref="LibUsbPortIdentity"/> chain token
    /// when the chain is valid, otherwise a <c>libusb-device:</c> fallback. Records that map to the same
    /// token are left to collapse to the first occurrence in <see cref="Build"/>.
    /// <para>为每条设备记录分配令牌：链有效时用规范的 <see cref="LibUsbPortIdentity"/> 链令牌，否则用
    /// <c>libusb-device:</c> 回退令牌。映射到同一令牌的记录交由 <see cref="Build"/> 折叠到首次出现。</para>
    /// </summary>
    private static IReadOnlyList<string> AssignTokens(IReadOnlyList<LibUsbTopologyDevice> devices)
    {
        var tokens = new string[devices.Count];
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            tokens[index] = LibUsbPortIdentity.Format(device.BusNumber, device.PortChain)
                ?? FallbackToken(device);
        }

        return tokens;
    }

    /// <summary>
    /// Builds the fallback identity of a device whose port chain cannot form a canonical token.
    /// <para>为端口链无法构成规范令牌的设备构造回退标识。</para>
    /// </summary>
    private static string FallbackToken(LibUsbTopologyDevice device)
    {
        var text = $"libusb-device:{device.BusNumber.ToString(CultureInfo.InvariantCulture)}"
            + $":{device.VendorId:X4}:{device.ProductId:X4}";
        return string.IsNullOrWhiteSpace(device.Serial) ? text : $"{text}:{device.Serial}";
    }

    /// <summary>
    /// Collects the tokens of observed devices that another observed device hangs below on the same bus,
    /// i.e. devices whose port chain is a strict prefix of a deeper chain. Such a device carries the
    /// <see cref="UsbTopologyNodeKind.Hub"/> role in the reconstructed topology even though libusb string
    /// and class data were not opened.
    /// <para>收集同一总线上挂着其他已观测设备的设备令牌，即端口链是更深链的严格前缀的设备。此类设备在重建
    /// 拓扑中承担 <see cref="UsbTopologyNodeKind.Hub"/> 角色，尽管 libusb 并未打开字符串与类数据。</para>
    /// </summary>
    private static HashSet<string> FindHostingTokens(IReadOnlyList<LibUsbTopologyDevice> devices,
        IReadOnlyList<string> tokens)
    {
        var tokensByChain = new Dictionary<(byte Bus, string Ports), string>();
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            if (LibUsbPortIdentity.Format(device.BusNumber, device.PortChain) is null)
            {
                continue;
            }

            tokensByChain.TryAdd((device.BusNumber, PortsKey(device.PortChain, device.PortChain.Count)),
                tokens[index]);
        }

        var hosting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (LibUsbPortIdentity.Format(device.BusNumber, device.PortChain) is null)
            {
                continue;
            }

            for (var length = 1; length < device.PortChain.Count; length++)
            {
                if (tokensByChain.TryGetValue((device.BusNumber, PortsKey(device.PortChain, length)),
                    out var ancestorToken))
                {
                    hosting.Add(ancestorToken);
                }
            }
        }

        return hosting;
    }

    /// <summary>
    /// Joins the first <paramref name="length"/> ports of a chain into a comparison key.
    /// <para>把端口链的前 <paramref name="length"/> 个端口拼成比较键。</para>
    /// </summary>
    private static string PortsKey(IReadOnlyList<byte> chain, int length)
        => string.Join(".", chain.Take(length)
            .Select(port => port.ToString(CultureInfo.InvariantCulture)));

    private static UsbTopologyNode BusRootNode(byte busNumber) => new()
    {
        InstanceId = BusRootInstanceId(busNumber),
        Kind = UsbTopologyNodeKind.RootHub,
        Description = "Bus " + busNumber.ToString("D3", CultureInfo.InvariantCulture)
    };

    private static UsbTopologyNode InferredHubNode(string instanceId, string parentInstanceId) => new()
    {
        InstanceId = instanceId,
        ParentInstanceId = parentInstanceId,
        Kind = UsbTopologyNodeKind.Hub,
        Description = null
    };

    private static UsbTopologyNode DeviceNode(LibUsbTopologyDevice device, string instanceId,
        string parentInstanceId, UsbTopologyNodeKind kind) => new()
        {
            InstanceId = instanceId,
            ParentInstanceId = parentInstanceId,
            Kind = kind,
            Description = null,
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            Revision = device.BcdDevice?.ToString("X4", CultureInfo.InvariantCulture),
            SerialNumber = device.Serial,
            Speed = device.Speed,
            HardwareIds = [BuildHardwareId(device, null)],
            LocationPaths = [instanceId]
        };

    private static UsbTopologyNode InterfaceNode(LibUsbTopologyDevice device, string instanceId,
        string deviceToken, LibUsbTopologyInterface iface) => new()
        {
            InstanceId = instanceId,
            ParentInstanceId = deviceToken,
            Kind = UsbTopologyNodeKind.Function,
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            Revision = device.BcdDevice?.ToString("X4", CultureInfo.InvariantCulture),
            SerialNumber = device.Serial,
            InterfaceNumber = iface.Number,
            InterfaceClass = iface.Class,
            InterfaceSubClass = iface.SubClass,
            InterfaceProtocol = iface.Protocol,
            Speed = device.Speed,
            HardwareIds = [BuildHardwareId(device, iface.Number)]
        };

    /// <summary>
    /// Builds the shared identity hardware id so <see cref="UsbTopologyNodes.Create"/> can see the same
    /// values the records carry explicitly: <c>USB\VID_&lt;vvvv&gt;&amp;PID_&lt;pppp&gt;</c>, extended by
    /// <c>&amp;REV_&lt;rrrr&gt;</c> on devices and <c>&amp;MI_&lt;nn&gt;</c> on interface functions.
    /// <para>构造共享的身份硬件 ID，使 <see cref="UsbTopologyNodes.Create"/> 能看到与记录里显式值一致的内容：
    /// <c>USB\VID_&lt;vvvv&gt;&amp;PID_&lt;pppp&gt;</c>，设备追加 <c>&amp;REV_&lt;rrrr&gt;</c>，接口功能追加
    /// <c>&amp;MI_&lt;nn&gt;</c>。</para>
    /// </summary>
    private static string BuildHardwareId(LibUsbTopologyDevice device, byte? interfaceNumber)
    {
        var text = $"USB\\VID_{device.VendorId:X4}&PID_{device.ProductId:X4}";
        if (interfaceNumber is { } number)
        {
            return text + $"&MI_{number:X2}";
        }

        return device.BcdDevice is { } release
            ? text + $"&REV_{release:X4}"
            : text;
    }
}