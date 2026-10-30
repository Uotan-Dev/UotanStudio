using UsbDeviceInfo = UotanStudio.Comm.Transports.Usb.Abstractions.UsbDeviceInfo;
using UsbEndpointInfo = UotanStudio.Comm.Transports.Usb.Abstractions.UsbEndpointInfo;
using UsbInterfaceInfo = UotanStudio.Comm.Transports.Usb.Abstractions.UsbInterfaceInfo;

namespace UotanStudio.Comm.Contracts;

/// <summary>
/// An exact route to one discovered transport endpoint, not a physical-device guess.
/// <para>指向单个已发现传输端点的精确路由，而不是对物理设备的猜测。</para>
/// </summary>
public sealed record DeviceEndpoint
{
    /// <summary>
    /// Name of the discovery source that reported this endpoint.
    /// <para>上报该端点的发现源名称。</para>
    /// </summary>
    /// <value>
    /// A required source identifier, such as the USB enumerator or a debug-bridge server source.
    /// <para>必填的来源标识，例如 USB 枚举器或调试桥 server 发现源。</para>
    /// </value>
    public required string Source { get; init; }
    /// <summary>
    /// Transport namespace the opener resolves when the endpoint is leased.
    /// <para>租用端点时由开启器解析的传输命名空间。</para>
    /// </summary>
    /// <value>
    /// A required non-blank key such as <c>adb-server</c>, <c>hdc-server</c>, or the name of the USB
    /// API kind that enumerated the device.
    /// <para>必填且非空的键，例如 <c>adb-server</c>、<c>hdc-server</c>，或枚举出该设备的 USB API
    /// 种类名称。</para>
    /// </value>
    public required string Transport { get; init; }
    /// <summary>
    /// Exact address of the endpoint inside its transport namespace.
    /// <para>端点在其传输命名空间内的精确地址。</para>
    /// </summary>
    /// <value>
    /// The server serial or connect key, or the USB device key; required and non-blank in a committed
    /// snapshot.
    /// <para>server 的 serial 或 connect key，或 USB 设备键；在已提交的快照中必填且非空。</para>
    /// </value>
    public required string Address { get; init; }
    /// <summary>
    /// Label the source suggests for user-visible listings.
    /// <para>发现源建议用于可见列表的显示名。</para>
    /// </summary>
    /// <value>
    /// Free-form display text, or <see langword="null"/> when the source exposes none.
    /// <para>自由文本形式的显示名；发现源未提供时为 <see langword="null"/>。</para>
    /// </value>
    public string? DisplayName { get; init; }
    /// <summary>
    /// Protocol observed by the source, when known.
    /// <para>发现源观察到的协议（若已知）。</para>
    /// </summary>
    /// <value>
    /// A protocol tag such as <c>adb</c> or <c>hdc</c>, or <see langword="null"/> when the source
    /// classified nothing.
    /// <para>协议标记，例如 <c>adb</c> 或 <c>hdc</c>；发现源未做出分类时为
    /// <see langword="null"/>。</para>
    /// </value>
    public string? Protocol { get; init; }
    /// <summary>
    /// Server or direct USB ownership; never inferred from a matching serial.
    /// <para>server 归属还是直连 USB 归属；绝不依据相匹配的 serial 推断。</para>
    /// </summary>
    /// <value>
    /// <c>server</c> when a debug-bridge server owns the target, <c>direct-usb</c> for a native
    /// USB open, or <see langword="null"/> when unreported.
    /// <para>调试桥 server 管理目标时为 <c>server</c>，原生 USB 直连时为 <c>direct-usb</c>，未报告归属时为 <see langword="null"/>。</para>
    /// </value>
    public string? Route { get; init; }
    /// <summary>
    /// Route snapshot epoch captured when the source projected this endpoint; zero when the route is
    /// not gated by a debug-bridge server snapshot. An open rejects an endpoint whose epoch no longer
    /// matches the current snapshot instead of silently opening across a changed route.
    /// <para>发现源投影该端点时捕获的路由快照 epoch；路由不受调试桥 server 快照门控时为零。
    /// 打开操作会拒绝 epoch 与当前快照不一致的端点，而不是跨已改变的路由静默打开。</para>
    /// </summary>
    /// <value>The snapshot epoch, or zero for an unstamped endpoint.
    /// <para>快照 epoch；未盖章的端点为 0。</para></value>
    public long RouteEpoch { get; init; }
    /// <summary>
    /// Target state text reported by the source.
    /// <para>发现源上报的目标状态文本。</para>
    /// </summary>
    /// <value>
    /// The state string as the server reported it, or <see langword="null"/> when the transport
    /// exposes no state.
    /// <para>server 原样上报的状态字符串；该传输不暴露状态时为 <see langword="null"/>。</para>
    /// </value>
    public string? State { get; init; }
    /// <summary>
    /// Serial or connect key exactly as the authoritative source reported it.
    /// <para>由权威发现源原样上报的 serial 或 connect key。</para>
    /// </summary>
    /// <value>
    /// The server routing identity string, or <see langword="null"/> when the endpoint exposes no
    /// serial.
    /// <para>server 路由所使用的身份字符串；该端点未暴露 serial 时为 <see langword="null"/>。</para>
    /// </value>
    public string? Serial { get; init; }
    /// <summary>
    /// Server-reported target transport (for example USB or TCP), not an opening route.
    /// <para>server 上报的目标传输（例如 USB 或 TCP），并非开启路由。</para>
    /// </summary>
    /// <value>
    /// The target transport text as reported by the server, or <see langword="null"/> when unknown;
    /// it never selects how the transport is opened.
    /// <para>server 上报的目标传输文本；未知时为 <see langword="null"/>；它不会决定传输的开启方式。</para>
    /// </value>
    public string? TargetTransport { get; init; }

    /// <summary>
    /// Network endpoint the server's own mDNS table resolves this transport to.
    /// <para>server 内置 mDNS 表为该传输解析出的网络端点。</para>
    /// </summary>
    /// <value>
    /// A <c>host:port</c> address, or <see langword="null"/> when the server reported no such
    /// resolution.
    /// <para><c>host:port</c> 形式的地址；server 未给出解析结果时为 <see langword="null"/>。</para>
    /// </value>
    public string? AliasAddress { get; init; }
    /// <summary>
    /// Observed USB identity backing this endpoint.
    /// <para>支撑该端点的已观测 USB 身份。</para>
    /// </summary>
    /// <value>
    /// The immutable USB details used to validate a selected by-key open, or <see langword="null"/>
    /// for endpoints owned by a debug-bridge server.
    /// <para>用于校验按所选键开启的不可变 USB 详细信息；由调试桥 server 管理的端点为
    /// <see langword="null"/>。</para>
    /// </value>
    public UsbEndpointDetails? Usb { get; init; }
    /// <summary>
    /// A defensive copy of the observed USB identity; mutations never alter the snapshot.
    /// <para>已观测 USB 身份的防御性副本；修改它永远不会影响快照。</para>
    /// </summary>
    /// <value>
    /// A copy of the native USB device information, or <see langword="null"/>.
    /// <para>原生 USB 设备信息的副本，或 <see langword="null"/>。</para>
    /// </value>
    public UsbDeviceInfo? UsbInfo
    {
        get => field is null ? null : UsbInfoCopy.Copy(field);
        init => field = value is null ? null : UsbInfoCopy.Copy(value);
    }

    /// <summary>
    /// Identity triple that leases this exact endpoint.
    /// <para>用于租用该精确端点的身份三元组。</para>
    /// </summary>
    /// <value>
    /// A <see cref="DeviceEndpointId"/> built from <see cref="Source"/>, <see cref="Transport"/> and
    /// <see cref="Address"/>; equal ids denote the same leaseable endpoint.
    /// <para>由 <see cref="Source"/>、<see cref="Transport"/> 与 <see cref="Address"/> 构成的
    /// <see cref="DeviceEndpointId"/>；相同的标识表示同一个可租用的端点。</para>
    /// </value>
    public DeviceEndpointId Id => new(Source, Transport, Address);

    /// <summary>Compares every recorded field by value, including the observed USB details.
    /// <para>按值比较全部已记录字段，其中包含已观测的 USB 详细信息。</para></summary>
    /// <param name="other">The endpoint to compare with this instance, or <see langword="null"/>.
    /// <para>与本实例比较的端点，或 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when every recorded field of the two endpoints carries the same value; otherwise <see langword="false"/>.
    /// <para>两个端点的全部已记录字段取值都相同时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></returns>
    public bool Equals(DeviceEndpoint? other)
    {
        if (other is null)
        {
            return false;
        }

        return Source == other.Source
            && Transport == other.Transport
            && Address == other.Address
            && DisplayName == other.DisplayName
            && Protocol == other.Protocol
            && Route == other.Route
            && State == other.State
            && Serial == other.Serial
            && TargetTransport == other.TargetTransport
            && AliasAddress == other.AliasAddress
            && Usb == other.Usb
            && UsbInfoEquals(UsbInfo, other.UsbInfo);
    }

    /// <summary>
    /// Hashes the recorded content so equal endpoints always share a hash.
    /// <para>对已记录内容计算哈希，使相等的端点始终具有相同哈希。</para>
    /// </summary>
    /// <returns>
    /// The hash of the same values compared by <see cref="Equals(DeviceEndpoint?)"/>, stable across
    /// repeated reads of <see cref="UsbInfo"/>.
    /// <para>由 <see cref="Equals(DeviceEndpoint?)"/> 所比较的相同值得到的哈希；重复读取
    /// <see cref="UsbInfo"/> 也不会改变它。</para>
    /// </returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Source);
        hash.Add(Transport);
        hash.Add(Address);
        hash.Add(DisplayName);
        hash.Add(Protocol);
        hash.Add(Route);
        hash.Add(State);
        hash.Add(Serial);
        hash.Add(TargetTransport);
        hash.Add(AliasAddress);
        hash.Add(Usb);
        hash.Add(UsbInfoHashCode(UsbInfo));
        return hash.ToHashCode();
    }

    /// <summary>
    /// Compares two observed USB details by content instead of by object identity.
    /// <para>按内容而非对象标识比较两份已观测 USB 详细信息。</para>
    /// </summary>
    /// <param name="left">
    /// The first copy, or <see langword="null"/>.
    /// <para>第一份副本，或 <see langword="null"/>。</para>
    /// </param>
    /// <param name="right">
    /// The second copy, or <see langword="null"/>.
    /// <para>第二份副本，或 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when both are <see langword="null"/> or every field that
    /// <see cref="UsbInfoCopy.Copy"/> preserves matches.
    /// <para>两者均为 <see langword="null"/>，或 <see cref="UsbInfoCopy.Copy"/> 保留的每个字段都相同
    /// 时为 <see langword="true"/>。</para>
    /// </returns>
    private static bool UsbInfoEquals(UsbDeviceInfo? left, UsbDeviceInfo? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.ApiName == right.ApiName
            && left.SourceApiKind == right.SourceApiKind
            && left.SourceDeviceType == right.SourceDeviceType
            && left.DevicePath == right.DevicePath
            && left.DeviceKey == right.DeviceKey
            && left.SerialNumber == right.SerialNumber
            && left.ContainerId == right.ContainerId
            && left.PhysicalPortPath == right.PhysicalPortPath
            && left.DeviceInstanceId == right.DeviceInstanceId
            && left.VendorId == right.VendorId
            && left.ProductId == right.ProductId
            && left.BcdUsb == right.BcdUsb
            && left.InterfaceClass == right.InterfaceClass
            && left.InterfaceSubClass == right.InterfaceSubClass
            && left.InterfaceProtocol == right.InterfaceProtocol
            && left.InterfaceMetadataObserved == right.InterfaceMetadataObserved
            && left.Speed == right.Speed
            && InterfacesEqual(left.Interfaces, right.Interfaces);
    }

    /// <summary>
    /// Compares interface tables element by element, keeping the discovery order significant.
    /// <para>逐元素比较接口表，并保持发现顺序的意义。</para>
    /// </summary>
    /// <param name="left">
    /// The interfaces recorded on one side.
    /// <para>一侧记录的接口列表。</para>
    /// </param>
    /// <param name="right">
    /// The interfaces recorded on the other side.
    /// <para>另一侧记录的接口列表。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when both tables hold the same interfaces in the same order.
    /// <para>两侧以相同顺序持有相同接口时为 <see langword="true"/>。</para>
    /// </returns>
    private static bool InterfacesEqual(IReadOnlyList<UsbInterfaceInfo> left,
        IReadOnlyList<UsbInterfaceInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.InterfaceNumber != second.InterfaceNumber
                || first.Class != second.Class
                || first.SubClass != second.SubClass
                || first.Protocol != second.Protocol
                || !EndpointsEqual(first.Endpoints, second.Endpoints))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares the endpoint descriptors of one interface.
    /// <para>比较单个接口的端点描述符。</para>
    /// </summary>
    /// <param name="left">
    /// The endpoints recorded on one side.
    /// <para>一侧记录的端点列表。</para>
    /// </param>
    /// <param name="right">
    /// The endpoints recorded on the other side.
    /// <para>另一侧记录的端点列表。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when both lists hold the same descriptors in the same order.
    /// <para>两侧以相同顺序持有相同描述符时为 <see langword="true"/>。</para>
    /// </returns>
    private static bool EndpointsEqual(IReadOnlyList<UsbEndpointInfo> left,
        IReadOnlyList<UsbEndpointInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.EndpointAddress != second.EndpointAddress
                || first.Attributes != second.Attributes
                || first.MaxPacketSize != second.MaxPacketSize
                || first.Interval != second.Interval)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Hashes exactly the fields <see cref="UsbInfoEquals"/> compares.
    /// <para>只对 <see cref="UsbInfoEquals"/> 所比较的字段计算哈希。</para>
    /// </summary>
    /// <param name="info">
    /// The USB details of this endpoint, or <see langword="null"/>.
    /// <para>本端点的 USB 详细信息，或 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// A content hash that is identical for equal USB details.
    /// <para>相同 USB 详细信息得到相同的内容哈希。</para>
    /// </returns>
    private static int UsbInfoHashCode(UsbDeviceInfo? info)
    {
        if (info is null)
        {
            return 0;
        }

        var hash = new HashCode();
        hash.Add(info.ApiName);
        hash.Add(info.SourceApiKind);
        hash.Add(info.SourceDeviceType);
        hash.Add(info.DevicePath);
        hash.Add(info.DeviceKey);
        hash.Add(info.SerialNumber);
        hash.Add(info.ContainerId);
        hash.Add(info.PhysicalPortPath);
        hash.Add(info.DeviceInstanceId);
        hash.Add(info.VendorId);
        hash.Add(info.ProductId);
        hash.Add(info.BcdUsb);
        hash.Add(info.InterfaceClass);
        hash.Add(info.InterfaceSubClass);
        hash.Add(info.InterfaceProtocol);
        hash.Add(info.InterfaceMetadataObserved);
        hash.Add(info.Speed);
        hash.Add(InterfacesHashCode(info.Interfaces));
        return hash.ToHashCode();
    }

    /// <summary>
    /// Hashes an interface table in discovery order.
    /// <para>按发现顺序对接口表计算哈希。</para>
    /// </summary>
    /// <param name="interfaces">
    /// The interfaces recorded for the device.
    /// <para>该设备记录的接口列表。</para>
    /// </param>
    /// <returns>
    /// A content hash that is identical for equal interface tables.
    /// <para>相同的接口表得到相同的内容哈希。</para>
    /// </returns>
    private static int InterfacesHashCode(IReadOnlyList<UsbInterfaceInfo> interfaces)
    {
        var hash = new HashCode();
        hash.Add(interfaces.Count);
        foreach (var iface in interfaces)
        {
            hash.Add(iface.InterfaceNumber);
            hash.Add(iface.Class);
            hash.Add(iface.SubClass);
            hash.Add(iface.Protocol);
            hash.Add(EndpointsHashCode(iface.Endpoints));
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Hashes the endpoint descriptors of one interface in discovery order.
    /// <para>按发现顺序对单个接口的端点描述符计算哈希。</para>
    /// </summary>
    /// <param name="endpoints">
    /// The endpoints recorded for the interface.
    /// <para>该接口记录的端点列表。</para>
    /// </param>
    /// <returns>
    /// A content hash that is identical for equal endpoint lists.
    /// <para>相同的端点列表得到相同的内容哈希。</para>
    /// </returns>
    private static int EndpointsHashCode(IReadOnlyList<UsbEndpointInfo> endpoints)
    {
        var hash = new HashCode();
        hash.Add(endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            hash.Add(endpoint.EndpointAddress);
            hash.Add(endpoint.Attributes);
            hash.Add(endpoint.MaxPacketSize);
            hash.Add(endpoint.Interval);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Identity includes the discovery source and transport namespace.
/// <para>标识包含发现源与传输命名空间。</para></summary>
/// <param name="Source">Name of the discovery source that reported the endpoint.
/// <para>上报该端点的发现源名称。</para></param>
/// <param name="Transport">Transport namespace key the opener resolves.
/// <para>开启器所解析的传输命名空间键。</para></param>
/// <param name="Address">Exact address of the endpoint inside that transport namespace.
/// <para>该端点在对应传输命名空间内的精确地址。</para></param>
public readonly record struct DeviceEndpointId(string Source, string Transport, string Address);