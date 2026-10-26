using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Models;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Devices;

/// <summary>
/// Device info model (wrapping a DetectedDevice enumerated by DeviceMonitorService).
/// <para>设备信息模型（包装 DeviceMonitorService 枚举出的 DetectedDevice）。</para>
/// </summary>
/// <remarks>
/// Initializes the device info.
/// <para>初始化设备信息。</para>
/// </remarks>
public partial class DeviceInfo(IReadOnlyList<DetectedDevice> endpoints, DeviceViewModel? owner = null) : ViewModelBase
{
    /// <summary>Creates a single-endpoint device entry.</summary>
    public DeviceInfo(DetectedDevice device, DeviceViewModel? owner = null) : this([device], owner) { }

    /// <summary>Verified protocol endpoints for this physical device.</summary>
    public IReadOnlyList<DetectedDevice> Connections { get; } = endpoints.Count > 0
        ? endpoints.ToArray() : throw new ArgumentException("A device requires an endpoint.", nameof(endpoints));

    /// <summary>The selected endpoint used for device operations.</summary>
    public DetectedDevice Device { get; private set; } = endpoints[0];

    /// <summary>
    /// The device target passed to device-facing services; it follows the currently selected endpoint.
    /// <para>传给面向设备服务的设备目标，随当前选中的端点变化。</para>
    /// </summary>
    public DeviceTarget Target => new(Connections, Device);

    /// <summary>
    /// Passes a device entry where a <see cref="DeviceTarget"/> is expected, keeping callers that still hold a
    /// <see cref="DeviceInfo"/> source-compatible.
    /// <para>在需要 <see cref="DeviceTarget"/> 的位置传入设备条目，使仍持有 <see cref="DeviceInfo"/> 的调用方保持兼容。</para>
    /// </summary>
    /// <param name="device">The device entry to convert. <para>要转换的设备条目。</para></param>
    public static implicit operator DeviceTarget(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Target;
    }

    /// <summary>Whether the device has more than one communication endpoint.</summary>
    public bool HasMultipleConnections => Connections.Count > 1;

    /// <summary>Chooses the protocol endpoint without changing the selected physical device.</summary>
    public DetectedDevice SelectedConnection
    {
        get => Device;
        set
        {
            var connection = Connections.FirstOrDefault(endpoint => ReferenceEquals(endpoint, value));
            if (connection is null && value is not null)
            {
                connection = Connections.FirstOrDefault(endpoint => endpoint.Key == value.Key);
            }
            if (connection is null || ReferenceEquals(Device, connection)) return;
            Device = connection;
            MtkHardware = string.Empty;
            MtkFirmware = string.Empty;
            MtkSecurityBits = string.Empty;
            MtkBootLoaderVersion = string.Empty;
            MtkBootRomVersion = string.Empty;
            MtkMeidHex = string.Empty;
            MtkSocidHex = string.Empty;
            EdlSerialPort = string.Empty;
            EdlSaharaVersion = string.Empty;
            EdlMaxCommandLength = string.Empty;
            EdlFirehoseVersion = string.Empty;
            EdlProgrammer = string.Empty;
            EdlStorageType = string.Empty;
            EdlStorageSerial = string.Empty;
            EdlStorageFirmware = string.Empty;
            EdlStorageManufacturer = string.Empty;
            EdlPageSize = string.Empty;
            EdlSectorSize = string.Empty;
            EdlCapacity = string.Empty;
            EdlMaxPayload = string.Empty;
            foreach (var property in new[]
            {
                nameof(Device), nameof(SelectedConnection), nameof(Serial), nameof(State),
                nameof(Mode), nameof(Protocol), nameof(Transport), nameof(DisplayName),
                nameof(ListTitle), nameof(ListSubtitle), nameof(LinkPort), nameof(UsbIds),
                nameof(VendorName), nameof(IsAdb), nameof(IsHdc), nameof(HasSystemInfo),
                nameof(IsFastboot), nameof(SupportsFastbootd), nameof(IsEdl), nameof(IsRkdl),
                nameof(IsUsb), nameof(IsNetwork), nameof(IsSerial), nameof(CanManageConnection),
                nameof(EdlProtocol), nameof(HasEdlProtocol), nameof(EdlBadge),
                nameof(EdlSaharaVersion), nameof(EdlMaxCommandLength), nameof(EdlFirehoseVersion),
                nameof(EdlProgrammer), nameof(EdlStorageType), nameof(EdlStorageSerial),
                nameof(EdlStorageFirmware), nameof(EdlStorageManufacturer), nameof(EdlPageSize),
                nameof(EdlSectorSize), nameof(EdlCapacity), nameof(EdlMaxPayload),
                nameof(IsMtk), nameof(HasMtkInfo), nameof(MtkHardware), nameof(MtkFirmware),
                nameof(MtkSecurityBits), nameof(MtkBootLoaderVersion), nameof(MtkBootRomVersion),
                nameof(MtkMeidHex), nameof(MtkSocidHex),
                nameof(IsIos), nameof(HasIosInfo), nameof(IosName), nameof(IosVersion),
                nameof(IosProductType), nameof(IosBattery), nameof(IosTrust)
            })
                OnPropertyChanged(property);
            owner?.NotifyConnectionChanged(this);
        }
    }

    internal bool MatchesConnections(IReadOnlyList<DetectedDevice> endpoints)
    {
        if (Connections.Count != endpoints.Count) return false;
        for (var index = 0; index < endpoints.Count; index++)
        {
            var current = Connections[index];
            var next = endpoints[index];
            if (current.Key != next.Key || current.Serial != next.Serial ||
                current.State != next.State || current.Protocol != next.Protocol ||
                current.Transport != next.Transport || current.EdlProtocol != next.EdlProtocol)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Gets or sets whether this entry is the globally selected device (top-bar selector
    /// check mark; selection changes are notified by <see cref="DeviceViewModel"/>).
    /// <para>获取或设置当前条目是否为全局选中设备（顶栏选择器打勾标记；
    /// 选中状态变化由 <see cref="DeviceViewModel"/> 统一通知）。</para>
    /// </summary>
    public bool IsSelected
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Called by <see cref="DeviceViewModel"/> when the selected device changes, refreshing
    /// the check-mark binding.
    /// <para>由 <see cref="DeviceViewModel"/> 在选中设备变化时调用，刷新打勾绑定。</para>
    /// </summary>
    internal void NotifySelectionChanged() => IsSelected = owner is not null && ReferenceEquals(owner.SelectedDevice, this);

    /// <summary>
    /// Selects this device (top-bar selector entry point; not executable for temporary
    /// instances without an owning <see cref="DeviceViewModel"/>).
    /// <para>选中本设备（顶栏选择器点选入口；无宿主 <see cref="DeviceViewModel"/> 的临时实例不可执行）。</para>
    /// </summary>
    [RelayCommand]
    private void Select()
    {
        if (owner is not null)
        {
            owner.SelectDevice(this);
        }
    }

    /// <summary>稳定键。 Stable key.</summary>
    public string Key => Connections[0].GroupId;

    /// <summary>设备序列号。 Device serial.</summary>
    public string Serial => Device.Serial;

    /// <summary>设备状态（device/offline/fastboot/edl/maskrom/loader 等）。 Device state.</summary>
    public string State
    {
        get => Device.State;
        set
        {
            if (Device.State != value)
            {
                Device.State = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ListSubtitle));
            }
        }
    }

    /// <summary>连接模式（ADB/Fastboot/EDL/RKDL）。 Connection mode.</summary>
    public string Mode => Device.ProtocolLabel;

    /// <summary>
    /// The COM port number when the EDL endpoint uses the QDLoader serial-driver fallback;
    /// empty over a native USB transport.
    /// <para>EDL 端点走 QDLoader 串口驱动回退时的 COM 端口号；原生 USB 传输时为空。</para>
    /// </summary>
    public string EdlSerialPort { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>协议类型枚举值。 Protocol kind value.</summary>
    public DeviceProtocolKind Protocol => Device.Protocol;

    /// <summary>传输类型（USB/Network/Serial）。 Transport kind (USB/Network/Serial).</summary>
    public string Transport => Device.Transport switch
    {
        DeviceTransportKind.Tcp => "Network",
        DeviceTransportKind.Serial => "Serial",
        DeviceTransportKind.Usb when EdlSerialPort.Length > 0 =>
            $"{Services.LanguageService.Instance.GetString("Device_TransportSerial")} ({EdlSerialPort})",
        DeviceTransportKind.Usb => "USB",
        _ => "Unknown",
    };

    /// <summary>设备型号（ro.product.model 或 fastboot product）。 Device model.</summary>
    public string Model
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ListTitle));
            }
        }
    } = string.Empty;

    /// <summary>Android 版本（ro.build.version.release）。 Android version.</summary>
    public string AndroidVersion
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>显示名称。 Display name.</summary>
    public string DisplayName => string.IsNullOrEmpty(Model)
        ? $"{Serial} ({State})"
        : $"{Serial} - {Model} ({State})";

    /// <summary>
    /// Device list entry title: the model when known, otherwise the serial.
    /// <para>设备列表条目标题：优先型号，型号未知时回退序列号。</para>
    /// </summary>
    public string ListTitle => string.IsNullOrEmpty(Model) ? Serial : Model;

    /// <summary>
    /// Device list entry subtitle: serial plus state for the entry's second row.
    /// <para>设备列表条目副标题：序列号与状态的组合，供条目第二行显示。</para>
    /// </summary>
    public string ListSubtitle => string.IsNullOrEmpty(State) ? Serial : $"{Serial} · {State}";

    /// <summary>链接端口/地址信息（USB 标识、TCP host:port、串口路径）。 Link/port info.</summary>
    public string LinkPort => Device.Transport switch
    {
        DeviceTransportKind.Tcp => Device.Serial,
        DeviceTransportKind.Serial => Device.Key,
        DeviceTransportKind.Usb when EdlSerialPort.Length > 0 => EdlSerialPort,
        DeviceTransportKind.Usb => "USB",
        _ => "Unknown",
    };

    /// <summary>厂商/产品 USB ID（十六进制 VID:PID）。 Vendor/product USB ID.</summary>
    public string UsbIds => Device.VendorId == 0
        ? string.Empty
        : $"{Device.VendorId:X04}:{Device.ProductId:X04}";

    /// <summary>USB 厂商名（按 VendorId 查内置表，未知返回空）。 USB vendor name from the built-in VID table.</summary>
    public string VendorName => UsbVendorTable.TryGetVendor(Device.VendorId);

    /// <summary>是否为 ADB 设备。 Whether the device is in ADB mode.</summary>
    public bool IsAdb => Device.Protocol == DeviceProtocolKind.Adb;

    /// <summary>Whether a noninteractive root shell was confirmed during the latest ADB scan.</summary>
    public bool HasSu
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Whether this is an HDC device.</summary>
    public bool IsHdc => Device.Protocol == DeviceProtocolKind.Hdc;

    /// <summary>Whether system details are available.</summary>
    public bool HasSystemInfo => IsAdb || IsHdc;

    /// <summary>是否为 Fastboot 设备。 Whether the device is in Fastboot mode.</summary>
    public bool IsFastboot => Device.Protocol == DeviceProtocolKind.Fastboot;

    /// <summary>
    /// Whether this fastboot device is the SpacemiT K1/K3 gadget (VID 0x361C, PID 0x1001).
    /// <para>该 fastboot 设备是否为 SpacemiT K1/K3 gadget（VID 0x361C、PID 0x1001）。</para>
    /// </summary>
    public bool IsSpacemiTFastboot =>
        IsFastboot && Device.VendorId == SpacemiTConstants.UsbVendorId && Device.ProductId == SpacemiTConstants.UsbProductId;

    /// <summary>
    /// Fastboot badge text: shows the SoC vendor "SpacemiT" for the K1/K3 gadget instead of the generic mode.
    /// <para>Fastboot 徽章文本：K1/K3 gadget 显示 SoC 厂商 "SpacemiT"，其余显示通用模式名。</para>
    /// </summary>
    public string FastbootBadge => IsSpacemiTFastboot ? "SpacemiT" : "Fastboot";

    /// <summary>
    /// Whether rebooting into fastbootd is supported (ADB or Fastboot protocol).
    /// <para>是否支持重启到 fastbootd（ADB 或 Fastboot 协议）。</para>
    /// </summary>
    public bool SupportsFastbootd => IsAdb || IsFastboot;

    /// <summary>是否为 EDL 设备。 Whether the device is in EDL mode.</summary>
    public bool IsEdl => Device.Protocol == DeviceProtocolKind.Edl;

    /// <summary>The latest verified EDL wire protocol.</summary>
    public EdlProtocolState EdlProtocol
    {
        get => Device.EdlProtocol;
        set
        {
            if (Device.EdlProtocol != value)
            {
                Device.EdlProtocol = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasEdlProtocol));
                OnPropertyChanged(nameof(EdlBadge));
            }
        }
    }

    /// <summary>Whether Sahara or Firehose has been verified for this endpoint.</summary>
    public bool HasEdlProtocol => IsEdl && EdlProtocol != EdlProtocolState.Unknown;

    /// <summary>
    /// EDL badge text: appends the Sahara/Firehose mode after EDL once verified.
    /// <para>EDL 徽章文本：已校验协议时在 EDL 后追加 Sahara/Firehose 模式。</para>
    /// </summary>
    public string EdlBadge => HasEdlProtocol ? $"EDL · {EdlProtocol}" : "EDL";

    /// <summary>
    /// The Sahara HELLO-reported protocol version; empty until collected.
    /// <para>Sahara HELLO 上报的协议版本，未采集时为空。</para>
    /// </summary>
    public string EdlSaharaVersion { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The Sahara HELLO-reported maximum command length (max_cmd_len); empty until collected.
    /// <para>Sahara HELLO 上报的最大命令长度（max_cmd_len），未采集时为空。</para>
    /// </summary>
    public string EdlMaxCommandLength { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The programmer version reported by Firehose configure; empty until collected.
    /// <para>Firehose configure 上报的编程器版本，未采集时为空。</para>
    /// </summary>
    public string EdlFirehoseVersion { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The Firehose programmer file name; empty until collected.
    /// <para>Firehose 编程器文件名，未采集时为空。</para>
    /// </summary>
    public string EdlProgrammer { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The storage type (getstorageinfo first, then configure); empty until collected.
    /// <para>存储类型（优先 getstorageinfo，其次 configure），未采集时为空。</para>
    /// </summary>
    public string EdlStorageType { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The storage serial number; empty until collected.
    /// <para>存储序列号，未采集时为空。</para>
    /// </summary>
    public string EdlStorageSerial { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The storage firmware version; empty until collected.
    /// <para>存储固件版本，未采集时为空。</para>
    /// </summary>
    public string EdlStorageFirmware { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The storage manufacturer identifier; empty until collected.
    /// <para>存储厂商标识，未采集时为空。</para>
    /// </summary>
    public string EdlStorageManufacturer { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The storage page size in bytes; empty until collected.
    /// <para>存储页大小（字节），未采集时为空。</para>
    /// </summary>
    public string EdlPageSize { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The storage block size in bytes; empty until collected.
    /// <para>存储块大小（字节），未采集时为空。</para>
    /// </summary>
    public string EdlSectorSize { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The total storage capacity (binary GiB plus raw bytes); empty until collected.
    /// <para>存储总容量（二进制 GiB 及原始字节数），未采集时为空。</para>
    /// </summary>
    public string EdlCapacity { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The Firehose-negotiated maximum payload in bytes; empty until collected.
    /// <para>Firehose 协商的最大载荷（字节），未采集时为空。</para>
    /// </summary>
    public string EdlMaxPayload { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Records the read-only EDL details collected alongside the probe; null means nothing was
    /// collected this time and existing values are kept.
    /// <para>将探测附加采集到的只读 EDL 详情写入模型；null 表示本次未采集到，保留原值。</para>
    /// </summary>
    /// <param name="details">The collected snapshot, or null. <para>采集到的快照，或 null。</para></param>
    internal void SetEdlDetails(EdlDetailsSnapshot? details)
    {
        if (details is null)
        {
            return;
        }
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var serialPort = details.SerialPortName ?? string.Empty;
        if (EdlSerialPort != serialPort)
        {
            EdlSerialPort = serialPort;
            OnPropertyChanged(nameof(Transport));
            OnPropertyChanged(nameof(LinkPort));
        }
        if (details.Sahara is { } sahara)
        {
            EdlSaharaVersion = sahara.ProtocolVersion.ToString(culture);
            EdlMaxCommandLength = sahara.MaxCommandLength.ToString(culture);
        }
        if (details.Capabilities is { } capabilities)
        {
            EdlFirehoseVersion = capabilities.FirehoseVersion ?? string.Empty;
            EdlProgrammer = capabilities.ProgrammerFileName ?? string.Empty;
            if (capabilities.MaxPayloadSizeToTargetInBytes is { } payload)
            {
                EdlMaxPayload = payload.ToString("N0", culture);
            }
        }
        if (details.Storage is { } storage)
        {
            EdlStorageType = storage.StorageType ?? storage.MemType ?? string.Empty;
            EdlStorageSerial = storage.SerialNumber ?? string.Empty;
            EdlStorageFirmware = storage.FirmwareVersion ?? string.Empty;
            EdlStorageManufacturer = storage.ManufacturerId ?? string.Empty;
            if (storage.PageSize is { } pageSize)
            {
                EdlPageSize = pageSize.ToString("N0", culture);
            }
        }
        if (details.Geometry is { } geometry)
        {
            EdlSectorSize = geometry.SectorSize.ToString("N0", culture);
            var capacity = geometry.CapacityBytes;
            EdlCapacity = string.Create(culture, $"{capacity / (1024.0 * 1024 * 1024):0.#} GiB ({capacity:N0} bytes)");
        }
        if (EdlStorageType.Length == 0 && details.Capabilities is not null)
        {
            EdlStorageType = details.Capabilities.StorageType.ToString();
        }
    }

    /// <summary>是否为 RKDL 设备。 Whether the device is in RKDL mode.</summary>
    public bool IsRkdl => Device.Protocol == DeviceProtocolKind.Rkdl;

    /// <summary>是否为 MTK 设备。 Whether the device is a MediaTek target.</summary>
    public bool IsMtk => Device.Protocol == DeviceProtocolKind.Mtk;

    /// <summary>是否为 iOS 设备。 Whether the device is an Apple (usbmuxd) target.</summary>
    public bool IsIos => Device.Protocol == DeviceProtocolKind.Ios;

    /// <summary>
    /// The MediaTek hardware code and subcode (<c>0x8A00 / 0x0001</c>); empty until queried.
    /// <para>MTK 硬件代码与子代码（<c>0x8A00 / 0x0001</c>），未查询时为空。</para>
    /// </summary>
    public string MtkHardware { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The MediaTek hardware and software revisions; empty until queried.
    /// <para>MTK 硬件与软件版本，未查询时为空。</para>
    /// </summary>
    public string MtkFirmware { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The MediaTek target security bits (<c>SBC/SLA/DAA/CERT</c>); empty until queried.
    /// <para>MTK 目标安全位（<c>SBC/SLA/DAA/CERT</c> 置位情况），未查询时为空。</para>
    /// </summary>
    public string MtkSecurityBits { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The MediaTek bootloader version byte (<c>0x1D</c>); empty when the handshake reported none.
    /// <para>MTK bootloader 版本字节（<c>0x1D</c>），握手未上报时为空。</para>
    /// </summary>
    public string MtkBootLoaderVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The MediaTek Boot ROM version byte (<c>0xFF</c>); empty when the handshake reported none.
    /// <para>MTK Boot ROM 版本字节（<c>0xFF</c>），握手未上报时为空。</para>
    /// </summary>
    public string MtkBootRomVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The MediaTek media device identifier (MEID, command <c>E1</c>) in lowercase hexadecimal; empty when
    /// the handshake reported none.
    /// <para>MTK 媒体设备标识（MEID，命令 <c>E1</c>）的小写十六进制，握手未上报时为空。</para>
    /// </summary>
    public string MtkMeidHex { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The MediaTek secure on-chip identifier (SOCID, command <c>E7</c>) in lowercase hexadecimal; empty when
    /// the handshake reported none.
    /// <para>MTK 安全片上标识（SOCID，命令 <c>E7</c>）的小写十六进制，握手未上报时为空。</para>
    /// </summary>
    public string MtkSocidHex { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>是否已查询到 MTK 身份信息。 Whether MediaTek identity info has been queried.</summary>
    public bool HasMtkInfo => MtkHardware.Length > 0;

    /// <summary>
    /// The iOS device name reported by lockdownd; empty until queried.
    /// <para>lockdownd 上报的 iOS 设备名称，未查询时为空。</para>
    /// </summary>
    public string IosName { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The iOS version reported by lockdownd; empty until queried.
    /// <para>lockdownd 上报的 iOS 版本，未查询时为空。</para>
    /// </summary>
    public string IosVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The hardware identifier (for example <c>iPhone15,3</c>); empty until queried.
    /// <para>硬件标识（例如 <c>iPhone15,3</c>），未查询时为空。</para>
    /// </summary>
    public string IosProductType { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The battery percentage as a short text like <c>86%</c>; empty until queried.
    /// <para>电量百分比短文本（如 <c>86%</c>），未查询时为空。</para>
    /// </summary>
    public string IosBattery { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// The trust state text; empty until queried.
    /// <para>信任状态文本，未查询时为空。</para>
    /// </summary>
    public string IosTrust { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>是否已查询到 iOS 设备信息。 Whether iOS device info has been queried.</summary>
    public bool HasIosInfo => IosName.Length > 0;

    /// <summary>
    /// Records the Apple device info obtained from lockdownd; labels come from the view.
    /// <para>记录 lockdownd 得到的 Apple 设备信息；标签由视图本地化。</para>
    /// </summary>
    /// <param name="info">The device info. <para>设备信息。</para></param>
    internal void SetIosInfo(UotanStudio.Comm.Contracts.AppleDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        IosName = info.Name ?? string.Empty;
        IosVersion = info.ProductVersion ?? string.Empty;
        IosProductType = info.ProductType ?? string.Empty;
        IosBattery = info.BatteryPercent is { } percent
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{percent}%")
            : string.Empty;
        IosTrust = info.TrustState switch
        {
            UotanStudio.Comm.Contracts.AppleTrustState.Trusted => "trusted",
            UotanStudio.Comm.Contracts.AppleTrustState.Untrusted => "untrusted",
            _ => "unpaired",
        };
        OnPropertyChanged(nameof(HasIosInfo));
    }

    /// <summary>
    /// <para>写入一次只读 Boot ROM 握手得到的 MTK 身份与安全位；值本身是技术串，标签由视图本地化。</para>
    /// Records the MediaTek identity and security bits obtained from one read-only Boot ROM
    /// handshake; the values themselves are technical strings and the labels come from the view.
    /// </summary>
    /// <param name="info">The identity reported by the target. <para>目标上报的身份。</para></param>
    internal void SetMtkInfo(MtkDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        MtkHardware = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"0x{info.HardwareCode:X4} / 0x{info.HardwareSubCode:X4}");
        MtkFirmware = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{info.HardwareVersion} / {info.SoftwareVersion}");
        MtkSecurityBits = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"SBC={(info.SecureBootEnabled ? 1 : 0)} SLA={(info.SerialLinkAuthenticationRequired ? 1 : 0)} " +
            $"DAA={(info.DownloadAgentAuthenticationRequired ? 1 : 0)} CERT={(info.RootCertificateRequired ? 1 : 0)}");
        MtkBootLoaderVersion = info.BlVersion is { } blVersion
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{blVersion:X2}")
            : string.Empty;
        MtkBootRomVersion = info.BromVersion is { } bromVersion
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{bromVersion:X2}")
            : string.Empty;
        MtkMeidHex = info.Meid is { } meid ? Convert.ToHexStringLower(meid) : string.Empty;
        MtkSocidHex = info.Socid is { } socid ? Convert.ToHexStringLower(socid) : string.Empty;
        OnPropertyChanged(nameof(HasMtkInfo));
    }

    /// <summary>是否为 USB 直连设备。 Whether the device is connected over USB.</summary>
    public bool IsUsb => Device.Transport == DeviceTransportKind.Usb;

    /// <summary>是否为网络（TCP/adb server）设备。 Whether the device is connected over network.</summary>
    public bool IsNetwork => Device.Transport == DeviceTransportKind.Tcp;

    /// <summary>Whether this endpoint supports explicit network connection management.</summary>
    public bool CanManageConnection => IsNetwork && Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc &&
        owner?.IsConnectionBusy != true;

    internal void NotifyConnectionAvailabilityChanged() => OnPropertyChanged(nameof(CanManageConnection));

    /// <summary>是否为串口设备。 Whether the device is connected over a serial port.</summary>
    public bool IsSerial => Device.Transport == DeviceTransportKind.Serial;

    /// <summary>
    /// Device brand (ro.product.brand).
    /// <para>设备品牌（ro.product.brand）。</para>
    /// </summary>
    public string Brand { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// System SDK version (ro.build.version.sdk).
    /// <para>系统 SDK 版本（ro.build.version.sdk）。</para>
    /// </summary>
    public string SdkVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Device codename (ro.product.device / fastboot product).
    /// <para>设备代号（ro.product.device / fastboot product）。</para>
    /// </summary>
    public string CodeName { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// CPU ABI (ro.product.cpu.abi).
    /// <para>CPU ABI（ro.product.cpu.abi）。</para>
    /// </summary>
    public string CpuAbi { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// SoC/board platform code (ro.board.platform).
    /// <para>SoC/主板平台代号（ro.board.platform）。</para>
    /// </summary>
    public string CpuCode { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Kernel version (uname -r).
    /// <para>内核版本（uname -r）。</para>
    /// </summary>
    public string Kernel { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Screen resolution (wm size).
    /// <para>屏幕分辨率（wm size）。</para>
    /// </summary>
    public string Resolution { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Screen pixel density (wm density).
    /// <para>屏幕像素密度（wm density）。</para>
    /// </summary>
    public string Density { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Board/SoC serial number.
    /// <para>主板/SoC 序列号。</para>
    /// </summary>
    public string BoardId { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Bootloader lock state.
    /// <para>Bootloader 锁定状态。</para>
    /// </summary>
    public string BlStatus { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Current A/B slot (e.g. _a/_b).
    /// <para>当前 A/B 槽位（如 _a/_b）。</para>
    /// </summary>
    public string Slot { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Power-on time (uptime).
    /// <para>开机时长（uptime）。</para>
    /// </summary>
    public string PowerOnTime { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Compile/build incremental version.
    /// <para>编译/构建增量版本号。</para>
    /// </summary>
    public string CompileVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// VNDK version.
    /// <para>VNDK 版本。</para>
    /// </summary>
    public string VndkVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Storage type (UFS/EMMC, from fastboot variant).
    /// <para>存储类型（UFS/EMMC，来自 fastboot variant）。</para>
    /// </summary>
    public string DiskType { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Whether fastboot is user-space (fastbootd).
    /// <para>fastboot 是否为 user-space（fastbootd）。</para>
    /// </summary>
    public string IsUserspace { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Battery level percent (-1 = unavailable).
    /// <para>电池电量百分比，-1 表示不可用。</para>
    /// </summary>
    public int BatteryLevel
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasBattery));
            }
        }
    } = -1;

    /// <summary>电池信息是否可用。 Whether battery info is available.</summary>
    public bool HasBattery => BatteryLevel >= 0;

    /// <summary>
    /// Battery status text (voltage/temperature).
    /// <para>电池状态文本（电压/温度）。</para>
    /// </summary>
    public string BatteryText { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Memory usage percent (-1 = unavailable).
    /// <para>内存占用百分比，-1 表示不可用。</para>
    /// </summary>
    public int MemLevel
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasMem));
            }
        }
    } = -1;

    /// <summary>内存信息是否可用。 Whether memory info is available.</summary>
    public bool HasMem => MemLevel >= 0;

    /// <summary>
    /// Memory usage text (used/total).
    /// <para>内存使用文本（已用/总量）。</para>
    /// </summary>
    public string MemText { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// Disk usage percent (-1 = unavailable).
    /// <para>磁盘占用百分比，-1 表示不可用。</para>
    /// </summary>
    public int DiskLevel
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasDisk));
            }
        }
    } = -1;

    /// <summary>磁盘信息是否可用。 Whether disk info is available.</summary>
    public bool HasDisk => DiskLevel >= 0;

    /// <summary>
    /// Whether at least one of battery/memory/storage resource metrics is available.
    /// <para>资源使用信息（电池/内存/存储）是否至少一项可用。</para>
    /// </summary>
    public bool HasResourceInfo => HasSystemInfo && (HasBattery || HasMem || HasDisk);

    /// <summary>
    /// Disk usage text (used/total).
    /// <para>磁盘占用文本（已用/总量）。</para>
    /// </summary>
    public string DiskText { get; set => SetProperty(ref field, value); } = string.Empty;
}