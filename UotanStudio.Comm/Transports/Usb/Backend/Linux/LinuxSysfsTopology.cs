using System.Globalization;
using System.Text;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

/// <summary>
/// The raw text read from one sysfs directory (a USB root hub, USB device, interface function or PCI host
/// controller). Property names follow the sysfs file names and every value keeps the text found in the file
/// until <see cref="LinuxSysfsTopology.Shape"/> parses it; attributes that cannot be read or parsed stay
/// <see langword="null"/> instead of being invented.
/// <para>一个 sysfs 目录（USB 根集线器、USB 设备、接口功能或 PCI 主机控制器）读到的原始文本。属性名沿用
/// sysfs 文件名，取值保持文件里的文本形式，直到 <see cref="LinuxSysfsTopology.Shape"/> 才解析；读不到或
/// 不可解析的属性一律保持 <see langword="null"/>，不伪造默认值。</para>
/// </summary>
internal sealed record LinuxSysfsEntry
{
    /// <summary>The sysfs path used as the stable node identity. <para>作为节点稳定标识的 sysfs 路径。</para></summary>
    /// <value>The resolved sysfs path, or the enumerated path when it is not a link. <para>已解析的 sysfs 路径；条目不是链接时即为枚举到的路径。</para></value>
    internal required string InstanceId { get; init; }

    /// <summary>The sysfs directory base name that carries the topology role. <para>承载拓扑角色的 sysfs 目录名。</para></summary>
    /// <value>A name such as <c>usb1</c>, <c>1-2</c>, <c>1-2.3</c>, <c>1-2:1.0</c> or <c>0000:00:14.0</c>. <para>形如 <c>usb1</c>、<c>1-2</c>、<c>1-2.3</c>、<c>1-2:1.0</c> 或 <c>0000:00:14.0</c> 的目录名。</para></value>
    internal required string Name { get; init; }

    /// <summary>The path as enumerated under the devices root. <para>设备根目录下枚举到的路径。</para></summary>
    /// <value>The enumerated path, or <see langword="null"/> for an ancestor discovered through a real path. Only
    /// reported as a location when it differs from <see cref="InstanceId"/>.
    /// <para>枚举到的路径；经真实路径发现的祖先节点为 <see langword="null"/>。仅当与 <see cref="InstanceId"/>
    /// 不同时才作为位置上报。</para></value>
    internal string? LinkPath { get; init; }

    /// <summary>The parent node identity, attached once the whole directory set is known. <para>父节点标识；在整套目录已知后写入。</para></summary>
    /// <value>The parent identity or <see langword="null"/> for a top-level node. <para>父标识；顶层节点为 <see langword="null"/>。</para></value>
    internal string? ParentInstanceId { get; init; }

    /// <summary>The <c>idVendor</c> text, or <c>vendor</c> on a PCI function. <para><c>idVendor</c> 文本；PCI 功能节点上为 <c>vendor</c>。</para></summary>
    /// <value>The hexadecimal attribute text or <see langword="null"/>. <para>十六进制属性文本或 <see langword="null"/>。</para></value>
    internal string? IdVendor { get; init; }

    /// <summary>The <c>idProduct</c> text, or <c>device</c> on a PCI function. <para><c>idProduct</c> 文本；PCI 功能节点上为 <c>device</c>。</para></summary>
    /// <value>The hexadecimal attribute text or <see langword="null"/>. <para>十六进制属性文本或 <see langword="null"/>。</para></value>
    internal string? IdProduct { get; init; }

    /// <summary>The <c>bcdDevice</c> text. <para><c>bcdDevice</c> 文本。</para></summary>
    /// <value>The release text as sysfs prints it or <see langword="null"/>. <para>sysfs 给出的版本文本或 <see langword="null"/>。</para></value>
    internal string? BcdDevice { get; init; }

    /// <summary>The <c>serial</c> text. <para><c>serial</c> 文本。</para></summary>
    /// <value>The serial number or <see langword="null"/>. <para>序列号或 <see langword="null"/>。</para></value>
    internal string? Serial { get; init; }

    /// <summary>The <c>manufacturer</c> string descriptor. <para><c>manufacturer</c> 字符串描述符。</para></summary>
    /// <value>The manufacturer text or <see langword="null"/>. <para>厂商文本或 <see langword="null"/>。</para></value>
    internal string? Manufacturer { get; init; }

    /// <summary>The <c>product</c> string descriptor. <para><c>product</c> 字符串描述符。</para></summary>
    /// <value>The product text or <see langword="null"/>. <para>产品文本或 <see langword="null"/>。</para></value>
    internal string? Product { get; init; }

    /// <summary>The <c>speed</c> text in Mbps. <para><c>speed</c> 文本，单位为 Mbps。</para></summary>
    /// <value>A decimal rate such as <c>480</c> or <c>1.5</c>, or <see langword="null"/>. <para>形如 <c>480</c> 或 <c>1.5</c> 的十进制速率或 <see langword="null"/>。</para></value>
    internal string? Speed { get; init; }

    /// <summary>The <c>bDeviceClass</c> text, or <c>class</c> on a PCI function. <para><c>bDeviceClass</c> 文本；PCI 功能节点上为 <c>class</c>。</para></summary>
    /// <value>The hexadecimal attribute text or <see langword="null"/>. <para>十六进制属性文本或 <see langword="null"/>。</para></value>
    internal string? BDeviceClass { get; init; }

    /// <summary>The <c>busnum</c> text. <para><c>busnum</c> 文本。</para></summary>
    /// <value>The decimal bus number or <see langword="null"/>. <para>十进制总线号或 <see langword="null"/>。</para></value>
    internal string? Busnum { get; init; }

    /// <summary>The <c>devnum</c> text. <para><c>devnum</c> 文本。</para></summary>
    /// <value>The decimal device address or <see langword="null"/>. <para>十进制设备地址或 <see langword="null"/>。</para></value>
    internal string? Devnum { get; init; }

    /// <summary>The <c>bInterfaceClass</c> text of an interface directory. <para>接口目录的 <c>bInterfaceClass</c> 文本。</para></summary>
    /// <value>The hexadecimal class text or <see langword="null"/>. <para>十六进制类文本或 <see langword="null"/>。</para></value>
    internal string? BInterfaceClass { get; init; }

    /// <summary>The <c>bInterfaceSubClass</c> text of an interface directory. <para>接口目录的 <c>bInterfaceSubClass</c> 文本。</para></summary>
    /// <value>The hexadecimal subclass text or <see langword="null"/>. <para>十六进制子类文本或 <see langword="null"/>。</para></value>
    internal string? BInterfaceSubClass { get; init; }

    /// <summary>The <c>bInterfaceProtocol</c> text of an interface directory. <para>接口目录的 <c>bInterfaceProtocol</c> 文本。</para></summary>
    /// <value>The hexadecimal protocol text or <see langword="null"/>. <para>十六进制协议文本或 <see langword="null"/>。</para></value>
    internal string? BInterfaceProtocol { get; init; }

    /// <summary>The <c>interface</c> string descriptor of an interface directory. <para>接口目录的 <c>interface</c> 字符串描述符。</para></summary>
    /// <value>The interface name text or <see langword="null"/>. <para>接口名文本或 <see langword="null"/>。</para></value>
    internal string? InterfaceDescription { get; init; }

    /// <summary>The bound kernel module name resolved from the <c>driver</c> entry. <para>由 <c>driver</c> 条目解析出的已绑定内核模块名。</para></summary>
    /// <value>The module name or <see langword="null"/> when no driver is bound. <para>模块名；未绑定驱动时为 <see langword="null"/>。</para></value>
    internal string? Driver { get; init; }
}

/// <summary>
/// The sysfs directory to shared topology node read and shaping layer. The directory naming rules
/// (<c>usbN</c> root hubs, <c>N-M</c> and <c>N-M.M</c> port-chain devices, <c>N-M:1.K</c> interface
/// functions, <c>0000:00:14.0</c> PCI functions), attribute text parsing, the speed thresholds, driver
/// binding and parentage resolution all live here, so a temp directory of regular files verifies it
/// completely without a real <c>/sys</c>. This layer only reads attribute files: it never opens a device
/// node, never issues an ioctl and never mounts anything, and a missing or unreadable attribute degrades
/// to <see langword="null"/> instead of throwing. Vendor, product, release, the interface triple, the
/// fallback role and protocol classification are still unified by <see cref="UsbTopologyNodes.Create"/>;
/// this layer writes only the values it read and marks the role explicitly where sysfs already states it.
/// <para>sysfs 目录到统一拓扑节点的读取与塑形层。目录名规则（<c>usbN</c> 根集线器、<c>N-M</c> 与
/// <c>N-M.M</c> 端口链设备、<c>N-M:1.K</c> 接口功能、<c>0000:00:14.0</c> PCI 功能）、属性文本解析、速度阈值、
/// 驱动绑定与父链推导都集中在这里，因此临时目录里的普通文件即可完整验证，无需真实 <c>/sys</c>。
/// 本层只读属性文件：不打开设备节点、不执行 ioctl、不挂载任何文件系统；缺失或不可读的属性降级为
/// <see langword="null"/> 而不抛异常。厂商/产品/版本/接口三元组、角色兜底与协议分类仍由
/// <see cref="UsbTopologyNodes.Create"/> 统一补齐，本层只写自己读到的原始值并显式标注已确定的角色。</para>
/// </summary>
internal static class LinuxSysfsTopology
{
    /// <summary>The sysfs USB devices root the backend enumerates by default. <para>后端默认枚举的 sysfs USB 设备根目录。</para></summary>
    /// <value>The absolute sysfs path. <para>sysfs 绝对路径。</para></value>
    internal const string DefaultDevicesRoot = "/sys/bus/usb/devices";

    private const int MaximumAttributeValueLength = 4096;

    /// <summary>
    /// Reads every USB topology node visible below the given sysfs devices root.
    /// <para>读取给定 sysfs USB 设备根目录下可见的全部 USB 拓扑节点。</para>
    /// </summary>
    /// <param name="devicesRoot">
    /// The sysfs devices root, typically <see cref="DefaultDevicesRoot"/>; a temp directory of regular
    /// files is accepted so the shaping is testable without a real <c>/sys</c>.
    /// <para>sysfs 设备根，通常为 <see cref="DefaultDevicesRoot"/>；也接受由普通文件构成的临时目录，
    /// 以便无需真实 <c>/sys</c> 即可验证塑形。</para>
    /// </param>
    /// <returns>
    /// Flat nodes keyed by sysfs path, empty when the root is absent; host-controller ancestors are
    /// included when the real paths expose them.
    /// <para>以 sysfs 路径为标识的扁平节点；根目录不存在时为空；真实路径暴露 PCI 祖先时一并包含主机控制器节点。</para>
    /// </returns>
    internal static IReadOnlyList<UsbTopologyNode> ReadFlatNodes(string devicesRoot)
    {
        if (string.IsNullOrWhiteSpace(devicesRoot) || !Directory.Exists(devicesRoot))
        {
            return [];
        }

        var idsByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var devices = new List<LinuxSysfsEntry>();
        foreach (var path in EnumerateChildDirectories(devicesRoot))
        {
            var name = Path.GetFileName(path);
            if (!IsUsbDirectoryName(name))
            {
                continue;
            }

            var linkPath = NormalizePath(path);
            var instanceId = TryResolveLinkTarget(path) ?? linkPath;
            if (idsByPath.ContainsKey(instanceId))
            {
                continue;
            }

            idsByPath[instanceId] = instanceId;
            idsByPath[linkPath] = instanceId;
            devices.Add(ReadDeviceEntry(instanceId, name, linkPath));
        }

        var hostControllers = ReadHostControllers(devices, idsByPath);
        var entries = new List<LinuxSysfsEntry>(devices.Count + hostControllers.Count);
        foreach (var device in devices)
        {
            entries.Add(device with { ParentInstanceId = ResolveParentInstanceId(device, idsByPath) });
        }

        entries.AddRange(hostControllers);

        var byInstanceId = new Dictionary<string, LinuxSysfsEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byInstanceId[entry.InstanceId] = entry;
        }

        var nodes = new List<UsbTopologyNode>(entries.Count);
        foreach (var entry in entries)
        {
            var parent = entry.ParentInstanceId is { Length: > 0 } parentId &&
                byInstanceId.TryGetValue(parentId, out var parentEntry)
                ? parentEntry
                : null;
            nodes.Add(Shape(entry, parent));
        }

        return nodes;
    }

    /// <summary>
    /// Shapes one sysfs entry into the flat contract node, inheriting from <paramref name="parent"/> only
    /// the identity an interface directory cannot expose itself (vendor, product, release, serial,
    /// manufacturer, bus and device address and the negotiated speed). sysfs publishes no Windows-style
    /// hardware ids, so <c>HardwareIds</c> and <c>CompatibleIds</c> carry the kernel modalias shape instead:
    /// <c>usb:vVVVVpPPPP</c> for a device, that prefix extended by <c>ic..isc..ip..in..</c> for an
    /// interface function, and <c>pci:vDDDDDDDDdDDDDDDDD</c> for a PCI host controller.
    /// <para>把单个 sysfs 条目塑形为扁平契约节点，仅从 <paramref name="parent"/> 继承接口目录自身无法给出的
    /// 标识（厂商/产品/版本/序列号/厂商名/总线与设备地址，以及协商速率）。sysfs 不发布 Windows 风格的硬件 ID，
    /// 因此 <c>HardwareIds</c> 与 <c>CompatibleIds</c> 改用内核 modalias 形状：设备为 <c>usb:vVVVVpPPPP</c>，
    /// 接口功能在该前缀后追加 <c>ic..isc..ip..in..</c>，PCI 主机控制器为
    /// <c>pci:vDDDDDDDDdDDDDDDDD</c>。</para>
    /// </summary>
    /// <param name="entry">
    /// The entry as read. <para>按原样读到的条目。</para>
    /// </param>
    /// <param name="parent">
    /// The parent entry when known, otherwise <see langword="null"/>. <para>已知时的父条目，否则为 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// The flat node with the role stated explicitly and unparsed attributes left <see langword="null"/>.
    /// <para>显式标注角色、未解析属性保持 <see langword="null"/> 的扁平节点。</para>
    /// </returns>
    internal static UsbTopologyNode Shape(LinuxSysfsEntry entry, LinuxSysfsEntry? parent)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var kind = InferKind(entry.Name, entry.BDeviceClass, entry.Driver);
        var source = kind == UsbTopologyNodeKind.Function ? parent : null;
        var vendorId = ParseHexUInt16(entry.IdVendor) ?? ParseHexUInt16(source?.IdVendor);
        var productId = ParseHexUInt16(entry.IdProduct) ?? ParseHexUInt16(source?.IdProduct);
        var interfaceClass = ParseHexByte(entry.BInterfaceClass);
        var interfaceSubClass = ParseHexByte(entry.BInterfaceSubClass);
        var interfaceProtocol = ParseHexByte(entry.BInterfaceProtocol);
        var interfaceNumber = kind == UsbTopologyNodeKind.Function ? ParseInterfaceNumber(entry.Name) : null;
        var speed = MapSpeed(entry.Speed);
        if (speed == UsbDeviceSpeed.Unknown && source is not null)
        {
            speed = MapSpeed(source.Speed);
        }

        var identity = BuildIdentity(kind, vendorId, productId);
        var classQualifier = BuildClassQualifier(interfaceClass, interfaceSubClass, interfaceProtocol);
        var interfaceQualifier = BuildInterfaceQualifier(interfaceClass, interfaceSubClass, interfaceProtocol,
            interfaceNumber);
        var hardwareId = identity.Length > 0
            ? identity + (interfaceQualifier.Length > 0 ? interfaceQualifier : classQualifier)
            : string.Empty;
        var compatibleId = identity.Length > 0 && classQualifier.Length > 0
            ? identity + classQualifier
            : string.Empty;

        var busNumber = ParseDecimalInt(entry.Busnum) ?? ParseDecimalInt(source?.Busnum);
        var deviceNumber = ParseDecimalInt(entry.Devnum) ?? ParseDecimalInt(source?.Devnum);

        return new UsbTopologyNode
        {
            InstanceId = entry.InstanceId,
            ParentInstanceId = entry.ParentInstanceId,
            Kind = kind,
            Description = kind == UsbTopologyNodeKind.Function ? entry.InterfaceDescription : entry.Product,
            ManufacturerName = entry.Manufacturer ?? source?.Manufacturer,
            ClassName = entry.Driver,
            DriverService = entry.Driver,
            VendorId = vendorId,
            ProductId = productId,
            Revision = entry.BcdDevice ?? source?.BcdDevice,
            SerialNumber = entry.Serial ?? source?.Serial,
            InterfaceNumber = interfaceNumber,
            InterfaceClass = interfaceClass,
            InterfaceSubClass = interfaceSubClass,
            InterfaceProtocol = interfaceProtocol,
            Speed = speed,
            HardwareIds = ToIdList(hardwareId),
            CompatibleIds = ToIdList(compatibleId),
            LocationPaths = BuildLocationPaths(entry, busNumber, deviceNumber)
        };
    }

    /// <summary>
    /// States the topology role from the sysfs directory name, the device class and the bound module:
    /// an interface directory is a function, <c>usbN</c> is a root hub, a PCI function name is a host
    /// controller, and a device is a hub when <c>bDeviceClass</c> is <c>09</c> or the <c>hub</c> module
    /// owns it.
    /// <para>由 sysfs 目录名、设备类与已绑定模块直接确定角色：接口目录为功能节点，<c>usbN</c> 为根集线器，
    /// PCI 功能名为主机控制器；<c>bDeviceClass</c> 为 <c>09</c> 或由 <c>hub</c> 模块持有该目录时设备为集线器。</para>
    /// </summary>
    /// <param name="name">
    /// The sysfs directory base name. <para>sysfs 目录名。</para>
    /// </param>
    /// <param name="deviceClassText">
    /// The <c>bDeviceClass</c> text of a USB directory, or the <c>class</c> text of a PCI function.
    /// <para>USB 目录的 <c>bDeviceClass</c> 文本，或 PCI 功能目录的 <c>class</c> 文本。</para>
    /// </param>
    /// <param name="driver">
    /// The bound module name. <para>已绑定模块名。</para>
    /// </param>
    /// <returns>
    /// The role stated by sysfs. <para>sysfs 已明确给出的角色。</para>
    /// </returns>
    internal static UsbTopologyNodeKind InferKind(string name, string? deviceClassText, string? driver)
    {
        if (IsInterfaceDirectoryName(name))
        {
            return UsbTopologyNodeKind.Function;
        }

        if (IsRootHubDirectoryName(name))
        {
            return UsbTopologyNodeKind.RootHub;
        }

        if (IsPciDirectoryName(name))
        {
            return UsbTopologyNodeKind.HostController;
        }

        if (ParseHexByte(deviceClassText) == 0x09 || string.Equals(driver, "hub", StringComparison.Ordinal))
        {
            return UsbTopologyNodeKind.Hub;
        }

        return UsbTopologyNodeKind.Device;
    }

    /// <summary>
    /// Maps the sysfs <c>speed</c> text, which reports the negotiated rate in Mbps and may be fractional,
    /// onto <see cref="UsbDeviceSpeed"/>: an absent or unparsable value, and any non-positive rate, is
    /// <see cref="UsbDeviceSpeed.Unknown"/>; otherwise the rate is bucketed by the ceiling of each
    /// generation, so at most 2 Mbps is <see cref="UsbDeviceSpeed.Low"/>, at most 12 Mbps
    /// <see cref="UsbDeviceSpeed.Full"/>, at most 480 Mbps <see cref="UsbDeviceSpeed.High"/>, at most
    /// 5000 Mbps <see cref="UsbDeviceSpeed.Super"/> and anything above that
    /// <see cref="UsbDeviceSpeed.SuperPlus"/>.
    /// <para>把 sysfs <c>speed</c> 文本（协商速率，单位 Mbps，允许小数）映射到 <see cref="UsbDeviceSpeed"/>：
    /// 缺失、不可解析或非正值均为 <see cref="UsbDeviceSpeed.Unknown"/>；其余按各代速率上限分桶——不超过 2 Mbps
    /// 为 <see cref="UsbDeviceSpeed.Low"/>，不超过 12 Mbps 为 <see cref="UsbDeviceSpeed.Full"/>，不超过 480 Mbps
    /// 为 <see cref="UsbDeviceSpeed.High"/>，不超过 5000 Mbps 为 <see cref="UsbDeviceSpeed.Super"/>，更高为
    /// <see cref="UsbDeviceSpeed.SuperPlus"/>。</para>
    /// </summary>
    /// <param name="text">
    /// The raw <c>speed</c> text. <para>原始 <c>speed</c> 文本。</para>
    /// </param>
    /// <returns>
    /// The mapped speed. <para>映射后的速度。</para>
    /// </returns>
    internal static UsbDeviceSpeed MapSpeed(string? text)
        => ParseMbps(text) is { } mbps && mbps > 0 ? UsbSpeedInference.FromMbps(mbps) : UsbDeviceSpeed.Unknown;

    /// <summary>
    /// Reads the bound module name of a directory. sysfs exposes <c>driver</c> as a symlink whose target
    /// basename is the module (for example <c>usb</c>, <c>hub</c>, <c>uas</c>, <c>cdc_acm</c>), so the
    /// target is preferred; a regular file holding the module name is accepted as well, because that is
    /// how a portable temp-directory fixture expresses driver binding.
    /// <para>读取目录的已绑定模块名。sysfs 的 <c>driver</c> 是指向模块目录的符号链接，目标基名即模块
    /// （例如 <c>usb</c>、<c>hub</c>、<c>uas</c>、<c>cdc_acm</c>），因此优先解析目标；同时容忍“普通文件里写着
    /// 模块名”的形式，因为可移植的临时目录夹具只能用普通文件表达驱动绑定。</para>
    /// </summary>
    /// <param name="directory">
    /// The sysfs directory holding the <c>driver</c> entry. <para>含有 <c>driver</c> 条目的 sysfs 目录。</para>
    /// </param>
    /// <returns>
    /// The module name, or <see langword="null"/> when no driver is bound or the entry cannot be read.
    /// <para>模块名；未绑定驱动或条目不可读时为 <see langword="null"/>。</para>
    /// </returns>
    internal static string? ReadDriverName(string directory)
    {
        var path = Path.Combine(directory, "driver");
        if (TryResolveLinkTarget(path) is { Length: > 0 } target)
        {
            var moduleName = Path.GetFileName(TrimTrailingSeparator(target));
            if (moduleName.Length > 0)
            {
                return moduleName;
            }
        }

        return ReadAttribute(directory, "driver");
    }

    /// <summary>
    /// Reads one attribute file and returns its first non-empty trimmed line.
    /// <para>读取单个属性文件并返回首个去空白后的非空行。</para>
    /// </summary>
    /// <param name="directory">
    /// The directory holding the attribute. <para>属性所在的目录。</para>
    /// </param>
    /// <param name="name">
    /// The attribute file name. <para>属性文件名。</para>
    /// </param>
    /// <returns>
    /// The attribute text, or <see langword="null"/> when the file is missing, empty or unreadable.
    /// <para>属性文本；文件缺失、为空或不可读时为 <see langword="null"/>。</para>
    /// </returns>
    internal static string? ReadAttribute(string directory, string name)
        => ReadFirstAttributeLine(Path.Combine(directory, name));

    /// <summary>
    /// Parses a sysfs hexadecimal attribute, tolerating the <c>0x</c> prefix a PCI function uses, into a
    /// <see cref="ushort"/>.
    /// <para>解析 sysfs 十六进制属性（容忍 PCI 功能使用的 <c>0x</c> 前缀）为 <see cref="ushort"/>。</para>
    /// </summary>
    /// <param name="text">
    /// The raw attribute text. <para>原始属性文本。</para>
    /// </param>
    /// <returns>
    /// The parsed value or <see langword="null"/>. <para>解析结果或 <see langword="null"/>。</para>
    /// </returns>
    internal static ushort? ParseHexUInt16(string? text)
        => StripHexPrefix(text) is { Length: > 0 } value &&
            ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Parses a sysfs hexadecimal attribute into a <see cref="byte"/>.
    /// <para>把 sysfs 十六进制属性解析为 <see cref="byte"/>。</para>
    /// </summary>
    /// <param name="text">
    /// The raw attribute text. <para>原始属性文本。</para>
    /// </param>
    /// <returns>
    /// The parsed value or <see langword="null"/> when it does not fit a single byte.
    /// <para>解析结果；无法容纳在单个字节内时为 <see langword="null"/>。</para>
    /// </returns>
    internal static byte? ParseHexByte(string? text)
        => StripHexPrefix(text) is { Length: > 0 } value &&
            byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Parses a sysfs decimal attribute such as <c>busnum</c> or <c>devnum</c>.
    /// <para>解析 sysfs 十进制属性，例如 <c>busnum</c> 或 <c>devnum</c>。</para>
    /// </summary>
    /// <param name="text">
    /// The raw attribute text. <para>原始属性文本。</para>
    /// </param>
    /// <returns>
    /// The parsed value or <see langword="null"/>. <para>解析结果或 <see langword="null"/>。</para>
    /// </returns>
    internal static int? ParseDecimalInt(string? text)
        => text?.Trim() is { Length: > 0 } value &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Reads the interface number out of the trailing <c>.K</c> component of an interface directory name
    /// such as <c>1-2:1.0</c>, since sysfs has no attribute for it.
    /// <para>由于 sysfs 没有对应属性，接口号从形如 <c>1-2:1.0</c> 的接口目录名尾部 <c>.K</c> 段读取。</para>
    /// </summary>
    /// <param name="name">
    /// The sysfs directory base name. <para>sysfs 目录名。</para>
    /// </param>
    /// <returns>
    /// The interface number or <see langword="null"/> for a non-interface name. <para>接口号；非接口名时为 <see langword="null"/>。</para>
    /// </returns>
    internal static byte? ParseInterfaceNumber(string name)
    {
        var colon = name.LastIndexOf(':');
        if (colon < 0 || colon + 1 >= name.Length)
        {
            return null;
        }

        var suffix = name[(colon + 1)..];
        var dot = suffix.LastIndexOf('.');
        if (dot < 0 || dot == suffix.Length - 1)
        {
            return null;
        }

        var text = suffix[(dot + 1)..];
        return text.Length <= 3 && byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture,
            out var number)
            ? number
            : null;
    }

    /// <summary>
    /// Reports whether a directory name belongs to the USB topology plane, that is a root hub, a port-chain
    /// device or an interface function.
    /// <para>报告目录名是否属于 USB 拓扑平面，即根集线器、端口链设备或接口功能。</para>
    /// </summary>
    /// <param name="name">
    /// The directory base name. <para>目录名。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the name is a USB topology directory. <para>为 USB 拓扑目录时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool IsUsbDirectoryName(string name)
        => IsRootHubDirectoryName(name) || IsDeviceDirectoryName(name) || IsInterfaceDirectoryName(name);

    /// <summary>
    /// Reports whether a name is a root-hub directory, that is <c>usb</c> followed by a bus number.
    /// <para>报告目录名是否为根集线器目录，即 <c>usb</c> 后接总线号。</para>
    /// </summary>
    /// <param name="name">
    /// The directory base name. <para>目录名。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> for <c>usbN</c>. <para>形如 <c>usbN</c> 时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool IsRootHubDirectoryName(string name)
        => name.StartsWith("usb", StringComparison.Ordinal) && IsDigits(name[3..], 1, 3);

    /// <summary>
    /// Reports whether a name is a device directory, that is a bus number, a dash and one or more
    /// dot-separated port numbers.
    /// <para>报告目录名是否为设备目录，即总线号、短横线，再加一个或多个以点分隔的端口号。</para>
    /// </summary>
    /// <param name="name">
    /// The directory base name. <para>目录名。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> for <c>N-M</c> or <c>N-M.M</c>. <para>形如 <c>N-M</c> 或 <c>N-M.M</c> 时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool IsDeviceDirectoryName(string name)
    {
        var dash = name.IndexOf('-');
        if (dash <= 0 || !IsDigits(name[..dash], 1, 3))
        {
            return false;
        }

        return name[(dash + 1)..].Split('.', StringSplitOptions.None).All(port => IsDigits(port, 1, 3));
    }

    /// <summary>
    /// Reports whether a name is an interface directory, that is a device or root-hub name, a colon and the
    /// <c>configuration.interface</c> pair. A PCI function name never matches.
    /// <para>报告目录名是否为接口目录，即设备或根集线器名、冒号，再加 <c>配置.接口</c> 对。PCI 功能名永不匹配。</para>
    /// </summary>
    /// <param name="name">
    /// The directory base name. <para>目录名。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> for <c>N-M:1.K</c>, <c>usbN:1.K</c> or <c>N:1.K</c>.
    /// <para>形如 <c>N-M:1.K</c>、<c>usbN:1.K</c> 或 <c>N:1.K</c> 时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool IsInterfaceDirectoryName(string name)
    {
        var colon = name.IndexOf(':');
        if (colon <= 0 || name.IndexOf(':', colon + 1) >= 0)
        {
            return false;
        }

        var deviceName = name[..colon];
        if (!IsRootHubDirectoryName(deviceName) && !IsDeviceDirectoryName(deviceName) &&
            !IsUsbBusNumber(deviceName))
        {
            return false;
        }

        var suffix = name[(colon + 1)..];
        var dot = suffix.IndexOf('.');
        return dot > 0 && IsUsbConfigurationValue(suffix[..dot]) && IsDigits(suffix[(dot + 1)..], 1, 3);
    }

    /// <summary>
    /// Root-hub interface directories in sysfs are named with a bare bus number, and USB bus numbers start
    /// at 1; requiring a non-zero value keeps PCI function names such as <c>00:14.0</c> from being read as
    /// interfaces.
    /// <para>sysfs 里根集线器的接口目录以裸总线号命名，而 USB 总线号从 1 开始；把它限制成非零数值可以
    /// 排除 PCI 功能名（如 <c>00:14.0</c>）被误判成接口。</para>
    /// </summary>
    /// <param name="text">
    /// The candidate bus number. <para>候选总线号。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the text is a one-to-three-digit number of value at least one.
    /// <para>文本为一到三位且数值不小于 1 时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool IsUsbBusNumber(string text)
        => IsDigits(text, 1, 3) && int.TryParse(text, out var value) && value >= 1;

    /// <summary>
    /// The segment after the colon in an interface directory is the configuration value, and USB
    /// configuration values start at 1, so a <c>00</c> segment is a PCI device.function rather than a USB
    /// interface.
    /// <para>接口目录冒号后的段是配置值，USB 配置值从 1 开始；因此 <c>00</c> 段一定不是 USB 接口，
    /// 而是 PCI 的 device.function。</para>
    /// </summary>
    /// <param name="text">
    /// The candidate configuration value. <para>候选配置值。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the text is a one-or-two-digit number of value at least one.
    /// <para>文本为一或两位且数值不小于 1 时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool IsUsbConfigurationValue(string text)
        => IsDigits(text, 1, 2) && int.TryParse(text, out var value) && value >= 1;

    /// <summary>
    /// Reports whether a directory name is a PCI function slot, with or without the domain prefix, e.g.
    /// <c>0000:00:14.0</c> or <c>00:14.0</c>.
    /// <para>报告目录名是否为 PCI 功能槽位（可带也可不带域前缀），例如 <c>0000:00:14.0</c> 或 <c>00:14.0</c>。</para>
    /// </summary>
    /// <param name="name">
    /// The directory base name. <para>目录名。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> for a PCI function name. <para>为 PCI 功能名时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool IsPciDirectoryName(string name)
    {
        var parts = name.Split(':');
        if (parts.Length is not (2 or 3))
        {
            return false;
        }

        var deviceAndFunction = parts[^1].Split('.', StringSplitOptions.None);
        if (deviceAndFunction.Length != 2 || deviceAndFunction[0].Length != 2 ||
            deviceAndFunction[1].Length != 1 || !IsHex(deviceAndFunction[0]) || !IsHex(deviceAndFunction[1]))
        {
            return false;
        }

        var bus = parts[^2];
        if (bus.Length != 2 || !IsHex(bus))
        {
            return false;
        }

        return parts.Length == 2 || (parts[0].Length == 4 && IsHex(parts[0]));
    }

    /// <summary>
    /// Derives the parent directory name from the sysfs naming convention: <c>1-2:1.0</c> and
    /// <c>usb1:1.0</c> belong to the device spelled before the colon, a root-hub interface written as just a
    /// bus number (<c>1:1.0</c>) belongs to <c>usb1</c>, a port chain drops its last port
    /// (<c>1-2.3</c> from <c>1-2</c>) and a first-level device (<c>1-2</c>) belongs to <c>usb1</c>. A root
    /// hub and a PCI function have no name-implied parent; the root hub is attached to its real-path host
    /// controller instead.
    /// <para>按 sysfs 命名约定推导父目录名：<c>1-2:1.0</c> 与 <c>usb1:1.0</c> 属于冒号前写出的设备；仅写总线号的
    /// 根集线器接口（<c>1:1.0</c>）属于 <c>usb1</c>；端口链去掉最后一个端口（<c>1-2.3</c> 的父为 <c>1-2</c>）；
    /// 一级设备（<c>1-2</c>）的父为 <c>usb1</c>。根集线器与 PCI 功能没有名字隐含的父节点，根集线器改为挂到
    /// 真实路径上的主机控制器。</para>
    /// </summary>
    /// <param name="name">
    /// The sysfs directory base name. <para>sysfs 目录名。</para>
    /// </param>
    /// <returns>
    /// The expected parent directory name or <see langword="null"/>. <para>预期的父目录名或 <see langword="null"/>。</para>
    /// </returns>
    internal static string? ParentDirectoryName(string name)
    {
        if (IsPciDirectoryName(name))
        {
            return null;
        }

        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            var deviceName = name[..colon];
            return IsDigits(deviceName, 1, 3) ? "usb" + deviceName : deviceName;
        }

        if (IsRootHubDirectoryName(name))
        {
            return null;
        }

        var dash = name.IndexOf('-');
        if (dash <= 0)
        {
            return null;
        }

        var ports = name[(dash + 1)..];
        var lastDot = ports.LastIndexOf('.');
        return lastDot < 0 ? "usb" + name[..dash] : $"{name[..dash]}-{ports[..lastDot]}";
    }

    /// <summary>
    /// Walks the directory components above a resolved sysfs path and returns the consecutive PCI function
    /// ancestors, outermost first, so a root hub keeps its host controller in the chain. Pure string work:
    /// the result of a <c>/sys</c> path is the same on every operating system.
    /// <para>向上遍历已解析 sysfs 路径的目录成分，返回连续的 PCI 功能祖先（外层在前），使根集线器保留自己的
    /// 主机控制器。纯字符串处理：<c>/sys</c> 路径在任何操作系统上的结果一致。</para>
    /// </summary>
    /// <param name="realPath">
    /// The resolved sysfs path of a directory. <para>目录的已解析 sysfs 路径。</para>
    /// </param>
    /// <returns>
    /// The ancestor paths in outermost-to-innermost order; empty when no PCI function is adjacent.
    /// <para>按外层到内层排列的祖先路径；相邻位置没有 PCI 功能时为空。</para>
    /// </returns>
    internal static IReadOnlyList<string> PciAncestorChain(string realPath)
    {
        var chain = new List<string>();
        var current = TrimLastSegment(realPath);
        while (current is { Length: > 0 } directory)
        {
            if (!IsPciDirectoryName(Path.GetFileName(directory)))
            {
                break;
            }

            chain.Insert(0, directory);
            current = TrimLastSegment(directory);
        }

        return chain;
    }

    private static List<LinuxSysfsEntry> ReadHostControllers(IReadOnlyList<LinuxSysfsEntry> devices,
        Dictionary<string, string> idsByPath)
    {
        var controllers = new List<LinuxSysfsEntry>();
        foreach (var device in devices)
        {
            if (!IsRootHubDirectoryName(device.Name))
            {
                continue;
            }

            var chain = PciAncestorChain(device.InstanceId);
            for (var index = 0; index < chain.Count; index++)
            {
                var path = NormalizePath(chain[index]);
                if (!idsByPath.TryAdd(path, path))
                {
                    continue;
                }

                var parentId = index > 0 &&
                    idsByPath.TryGetValue(NormalizePath(chain[index - 1]), out var known)
                    ? known
                    : null;
                controllers.Add(ReadHostControllerEntry(path) with { ParentInstanceId = parentId });
            }
        }

        return controllers;
    }

    private static LinuxSysfsEntry ReadDeviceEntry(string instanceId, string name, string linkPath)
    {
        var isInterface = IsInterfaceDirectoryName(name);
        return new LinuxSysfsEntry
        {
            InstanceId = instanceId,
            Name = name,
            LinkPath = linkPath,
            IdVendor = ReadAttribute(linkPath, "idVendor"),
            IdProduct = ReadAttribute(linkPath, "idProduct"),
            BcdDevice = ReadAttribute(linkPath, "bcdDevice"),
            Serial = ReadAttribute(linkPath, "serial"),
            Manufacturer = ReadAttribute(linkPath, "manufacturer"),
            Product = ReadAttribute(linkPath, "product"),
            Speed = ReadAttribute(linkPath, "speed"),
            BDeviceClass = ReadAttribute(linkPath, "bDeviceClass"),
            Busnum = ReadAttribute(linkPath, "busnum"),
            Devnum = ReadAttribute(linkPath, "devnum"),
            BInterfaceClass = isInterface ? ReadAttribute(linkPath, "bInterfaceClass") : null,
            BInterfaceSubClass = isInterface ? ReadAttribute(linkPath, "bInterfaceSubClass") : null,
            BInterfaceProtocol = isInterface ? ReadAttribute(linkPath, "bInterfaceProtocol") : null,
            InterfaceDescription = isInterface ? ReadAttribute(linkPath, "interface") : null,
            Driver = ReadDriverName(linkPath)
        };
    }

    private static LinuxSysfsEntry ReadHostControllerEntry(string path) => new()
    {
        InstanceId = path,
        Name = Path.GetFileName(path),
        IdVendor = ReadAttribute(path, "vendor"),
        IdProduct = ReadAttribute(path, "device"),
        BDeviceClass = ReadAttribute(path, "class"),
        Driver = ReadDriverName(path)
    };

    private static string? ResolveParentInstanceId(LinuxSysfsEntry entry, Dictionary<string, string> idsByPath)
    {
        var parentName = ParentDirectoryName(entry.Name);
        foreach (var directory in CandidateParentDirectories(entry))
        {
            if (parentName is null)
            {
                if (idsByPath.TryGetValue(directory, out var containingId))
                {
                    return containingId;
                }

                continue;
            }

            if (string.Equals(Path.GetFileName(directory), parentName, StringComparison.Ordinal) &&
                idsByPath.TryGetValue(directory, out var containedId))
            {
                return containedId;
            }

            if (Combine(directory, parentName) is { Length: > 0 } sibling &&
                idsByPath.TryGetValue(sibling, out var siblingId))
            {
                return siblingId;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> CandidateParentDirectories(LinuxSysfsEntry entry)
    {
        var directories = new List<string>(2);
        AddCandidateDirectory(directories, TrimLastSegment(entry.InstanceId));
        AddCandidateDirectory(directories, TrimLastSegment(entry.LinkPath ?? entry.InstanceId));
        return directories;
    }

    private static void AddCandidateDirectory(List<string> directories, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return;
        }

        var normalized = NormalizePath(candidate);
        if (normalized.Length > 0 && !directories.Contains(normalized))
        {
            directories.Add(normalized);
        }
    }

    private static IReadOnlyList<string> ToIdList(string value)
    {
        var ids = new List<string>(1);
        if (value.Length > 0)
        {
            ids.Add(value);
        }

        return ids;
    }

    /// <summary>
    /// Builds the location entries of a node: the <c>sysfs:</c> alias path when the instance id is the
    /// resolved real path, and the <c>usbfs:</c> transfer-node path the kernel exposes for the same bus
    /// address. Attribute values that are not paths stay out of this list, because the shared field means
    /// physical location on every platform.
    /// <para>构建节点的位置条目：实例标识为已解析真实路径时给出 <c>sysfs:</c> 别名路径，以及内核为同一总线地址
    /// 暴露的 <c>usbfs:</c> 传输节点路径。非路径的属性值不进入该列表，因为共享字段在各平台都表示物理位置。</para>
    /// </summary>
    private static IReadOnlyList<string> BuildLocationPaths(LinuxSysfsEntry entry, int? busNumber,
        int? deviceNumber)
    {
        var paths = new List<string>(2);
        if (entry.LinkPath is { Length: > 0 } linkPath &&
            !string.Equals(linkPath, entry.InstanceId, StringComparison.Ordinal))
        {
            paths.Add($"sysfs:{linkPath}");
        }

        if (busNumber is > 0 && deviceNumber is > 0)
        {
            paths.Add($"usbfs:/dev/bus/usb/{busNumber:D3}/{deviceNumber:D3}");
        }

        return paths;
    }

    private static string BuildIdentity(UsbTopologyNodeKind kind, ushort? vendorId, ushort? productId)
    {
        if (vendorId is null)
        {
            return string.Empty;
        }

        var vendor = vendorId.Value;
        if (kind == UsbTopologyNodeKind.HostController)
        {
            return productId is { } pciDevice ? $"pci:v{vendor:X8}d{pciDevice:X8}" : $"pci:v{vendor:X8}";
        }

        return productId is { } product ? $"usb:v{vendor:X4}p{product:X4}" : $"usb:v{vendor:X4}";
    }

    private static string BuildClassQualifier(byte? interfaceClass, byte? interfaceSubClass,
        byte? interfaceProtocol)
        => interfaceClass is { } cls
            ? $"ic{cls:X2}isc{interfaceSubClass ?? 0:X2}ip{interfaceProtocol ?? 0:X2}"
            : string.Empty;

    private static string BuildInterfaceQualifier(byte? interfaceClass, byte? interfaceSubClass,
        byte? interfaceProtocol, byte? interfaceNumber)
        => interfaceClass is { } cls && interfaceNumber is { } number
            ? $"ic{cls:X2}isc{interfaceSubClass ?? 0:X2}ip{interfaceProtocol ?? 0:X2}in{number:X2}"
            : string.Empty;

    private static double? ParseMbps(string? text)
        => text?.Trim() is { Length: > 0 } value && double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var mbps)
            ? mbps
            : null;

    private static string? StripHexPrefix(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var stripped = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return stripped.Length > 0 ? stripped : null;
    }

    private static string? ReadFirstAttributeLine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            var buffer = new byte[MaximumAttributeValueLength];
            var read = stream.Read(buffer.AsSpan());
            if (read <= 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var newLine = text.IndexOf('\n');
            var value = (newLine >= 0 ? text[..newLine] : text).Trim();
            return value.Length > 0 ? value : null;
        }
        catch (Exception ex) when (IsIgnorableIoFailure(ex))
        {
            return null;
        }
    }

    private static IReadOnlyList<string> EnumerateChildDirectories(string root)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(root)];
        }
        catch (Exception ex) when (IsIgnorableIoFailure(ex))
        {
            return [];
        }
    }

    private static string? TryResolveLinkTarget(string path)
    {
        var target = TryResolve(() => Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName)
            ?? TryResolve(() => File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName);
        return target is { Length: > 0 } resolved ? NormalizePath(resolved) : null;
    }

    private static string? TryResolve(Func<string?> resolve)
    {
        try
        {
            return resolve();
        }
        catch (Exception ex) when (IsIgnorableIoFailure(ex))
        {
            return null;
        }
    }

    private static string? Combine(string directory, string name)
    {
        try
        {
            return NormalizePath(Path.Combine(directory, name));
        }
        catch (Exception ex) when (IsIgnorableIoFailure(ex))
        {
            return null;
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return TrimTrailingSeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (IsIgnorableIoFailure(ex))
        {
            return TrimTrailingSeparator(path);
        }
    }

    private static string TrimTrailingSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? TrimLastSegment(string path)
    {
        var trimmed = TrimTrailingSeparator(path);
        var index = Math.Max(trimmed.LastIndexOf(Path.DirectorySeparatorChar),
            trimmed.LastIndexOf(Path.AltDirectorySeparatorChar));
        return index <= 0 ? null : trimmed[..index];
    }

    private static bool IsIgnorableIoFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or PathTooLongException;

    private static bool IsDigits(string text, int minimum, int maximum)
        => text.Length >= minimum && text.Length <= maximum && text.All(character => character is >= '0' and <= '9');

    private static bool IsHex(string text) => text.Length > 0 && text.All(Uri.IsHexDigit);
}