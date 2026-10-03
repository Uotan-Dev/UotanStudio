using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Models;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Devices;

/// <summary>
/// <para>设备信息模型（包装 DeviceMonitorService 枚举出的 DetectedDevice）。</para>
/// Device info model (wrapping a DetectedDevice enumerated by DeviceMonitorService).
/// </summary>
/// <remarks>
/// <para>初始化设备信息。</para>
/// Initializes the device info.
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
                nameof(IsMtk), nameof(HasMtkInfo)
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
    /// <para>获取或设置当前条目是否为全局选中设备（顶栏选择器打勾标记；
    /// 选中状态变化由 <see cref="DeviceViewModel"/> 统一通知）。</para>
    /// Gets or sets whether this entry is the globally selected device (top-bar selector
    /// check mark; selection changes are notified by <see cref="DeviceViewModel"/>).
    /// </summary>
    public bool IsSelected
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>由 <see cref="DeviceViewModel"/> 在选中设备变化时调用，刷新打勾绑定。</para>
    /// Called by <see cref="DeviceViewModel"/> when the selected device changes, refreshing
    /// the check-mark binding.
    /// </summary>
    internal void NotifySelectionChanged() => IsSelected = owner is not null && ReferenceEquals(owner.SelectedDevice, this);

    /// <summary>
    /// <para>选中本设备（顶栏选择器点选入口；无宿主 <see cref="DeviceViewModel"/> 的临时实例不可执行）。</para>
    /// Selects this device (top-bar selector entry point; not executable for temporary
    /// instances without an owning <see cref="DeviceViewModel"/>).
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
    /// <para>EDL 端点走 QDLoader 串口驱动回退时的 COM 端口号；原生 USB 传输时为空。</para>
    /// The COM port number when the EDL endpoint uses the QDLoader serial-driver fallback;
    /// empty over a native USB transport.
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
            $"{UotanStudio.Services.LanguageService.Instance.GetString("Device_TransportSerial")} ({EdlSerialPort})",
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
    /// <para>设备列表条目标题：优先型号，型号未知时回退序列号。</para>
    /// Device list entry title: the model when known, otherwise the serial.
    /// </summary>
    public string ListTitle => string.IsNullOrEmpty(Model) ? Serial : Model;

    /// <summary>
    /// <para>设备列表条目副标题：序列号与状态的组合，供条目第二行显示。</para>
    /// Device list entry subtitle: serial plus state for the entry's second row.
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
    /// <para>是否支持重启到 fastbootd（ADB 或 Fastboot 协议）。</para>
    /// Whether rebooting into fastbootd is supported (ADB or Fastboot protocol).
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
    /// <para>EDL 徽章文本：已校验协议时在 EDL 后追加 Sahara/Firehose 模式。</para>
    /// EDL badge text: appends the Sahara/Firehose mode after EDL once verified.
    /// </summary>
    public string EdlBadge => HasEdlProtocol ? $"EDL · {EdlProtocol}" : "EDL";

    /// <summary>
    /// <para>Sahara HELLO 上报的协议版本，未采集时为空。</para>
    /// The Sahara HELLO-reported protocol version; empty until collected.
    /// </summary>
    public string EdlSaharaVersion { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>Sahara HELLO 上报的最大命令长度（max_cmd_len），未采集时为空。</para>
    /// The Sahara HELLO-reported maximum command length (max_cmd_len); empty until collected.
    /// </summary>
    public string EdlMaxCommandLength { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>Firehose configure 上报的编程器版本，未采集时为空。</para>
    /// The programmer version reported by Firehose configure; empty until collected.
    /// </summary>
    public string EdlFirehoseVersion { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>Firehose 编程器文件名，未采集时为空。</para>
    /// The Firehose programmer file name; empty until collected.
    /// </summary>
    public string EdlProgrammer { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储类型（优先 getstorageinfo，其次 configure），未采集时为空。</para>
    /// The storage type (getstorageinfo first, then configure); empty until collected.
    /// </summary>
    public string EdlStorageType { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储序列号，未采集时为空。</para>
    /// The storage serial number; empty until collected.
    /// </summary>
    public string EdlStorageSerial { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储固件版本，未采集时为空。</para>
    /// The storage firmware version; empty until collected.
    /// </summary>
    public string EdlStorageFirmware { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储厂商标识，未采集时为空。</para>
    /// The storage manufacturer identifier; empty until collected.
    /// </summary>
    public string EdlStorageManufacturer { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储页大小（字节），未采集时为空。</para>
    /// The storage page size in bytes; empty until collected.
    /// </summary>
    public string EdlPageSize { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储块大小（字节），未采集时为空。</para>
    /// The storage block size in bytes; empty until collected.
    /// </summary>
    public string EdlSectorSize { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储总容量（二进制 GiB 及原始字节数），未采集时为空。</para>
    /// The total storage capacity (binary GiB plus raw bytes); empty until collected.
    /// </summary>
    public string EdlCapacity { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>Firehose 协商的最大载荷（字节），未采集时为空。</para>
    /// The Firehose-negotiated maximum payload in bytes; empty until collected.
    /// </summary>
    public string EdlMaxPayload { get; private set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>将探测附加采集到的只读 EDL 详情写入模型；null 表示本次未采集到，保留原值。</para>
    /// Records the read-only EDL details collected alongside the probe; null means nothing was
    /// collected this time and existing values are kept.
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

    /// <summary>
    /// <para>MTK 硬件代码与子代码（<c>0x8A00 / 0x0001</c>），未查询时为空。</para>
    /// The MediaTek hardware code and subcode (<c>0x8A00 / 0x0001</c>); empty until queried.
    /// </summary>
    public string MtkHardware { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>MTK 硬件与软件版本，未查询时为空。</para>
    /// The MediaTek hardware and software revisions; empty until queried.
    /// </summary>
    public string MtkFirmware { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>MTK 目标安全位（<c>SBC/SLA/DAA/CERT</c> 置位情况），未查询时为空。</para>
    /// The MediaTek target security bits (<c>SBC/SLA/DAA/CERT</c>); empty until queried.
    /// </summary>
    public string MtkSecurityBits { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>是否已查询到 MTK 身份信息。 Whether MediaTek identity info has been queried.</summary>
    public bool HasMtkInfo => MtkHardware.Length > 0;

    /// <summary>
    /// <para>写入一次只读 Boot ROM 握手得到的 MTK 身份与安全位；值本身是技术串，标签由视图本地化。</para>
    /// Records the MediaTek identity and security bits obtained from one read-only Boot ROM
    /// handshake; the values themselves are technical strings and the labels come from the view.
    /// </summary>
    /// <param name="info">The identity reported by the target. <para>目标上报的身份。</param>
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
    /// <para>设备品牌（ro.product.brand）。</para>
    /// Device brand (ro.product.brand).
    /// </summary>
    public string Brand { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>系统 SDK 版本（ro.build.version.sdk）。</para>
    /// System SDK version (ro.build.version.sdk).
    /// </summary>
    public string SdkVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>设备代号（ro.product.device / fastboot product）。</para>
    /// Device codename (ro.product.device / fastboot product).
    /// </summary>
    public string CodeName { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>CPU ABI（ro.product.cpu.abi）。</para>
    /// CPU ABI (ro.product.cpu.abi).
    /// </summary>
    public string CpuAbi { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>SoC/主板平台代号（ro.board.platform）。</para>
    /// SoC/board platform code (ro.board.platform).
    /// </summary>
    public string CpuCode { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>内核版本（uname -r）。</para>
    /// Kernel version (uname -r).
    /// </summary>
    public string Kernel { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>屏幕分辨率（wm size）。</para>
    /// Screen resolution (wm size).
    /// </summary>
    public string Resolution { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>屏幕像素密度（wm density）。</para>
    /// Screen pixel density (wm density).
    /// </summary>
    public string Density { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>主板/SoC 序列号。</para>
    /// Board/SoC serial number.
    /// </summary>
    public string BoardId { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>Bootloader 锁定状态。</para>
    /// Bootloader lock state.
    /// </summary>
    public string BlStatus { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>当前 A/B 槽位（如 _a/_b）。</para>
    /// Current A/B slot (e.g. _a/_b).
    /// </summary>
    public string Slot { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>开机时长（uptime）。</para>
    /// Power-on time (uptime).
    /// </summary>
    public string PowerOnTime { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>编译/构建增量版本号。</para>
    /// Compile/build incremental version.
    /// </summary>
    public string CompileVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>VNDK 版本。</para>
    /// VNDK version.
    /// </summary>
    public string VndkVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储类型（UFS/EMMC，来自 fastboot variant）。</para>
    /// Storage type (UFS/EMMC, from fastboot variant).
    /// </summary>
    public string DiskType { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>fastboot 是否为 user-space（fastbootd）。</para>
    /// Whether fastboot is user-space (fastbootd).
    /// </summary>
    public string IsUserspace { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>电池电量百分比，-1 表示不可用。</para>
    /// Battery level percent (-1 = unavailable).
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
    /// <para>电池状态文本（电压/温度）。</para>
    /// Battery status text (voltage/temperature).
    /// </summary>
    public string BatteryText { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>内存占用百分比，-1 表示不可用。</para>
    /// Memory usage percent (-1 = unavailable).
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
    /// <para>内存使用文本（已用/总量）。</para>
    /// Memory usage text (used/total).
    /// </summary>
    public string MemText { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>磁盘占用百分比，-1 表示不可用。</para>
    /// Disk usage percent (-1 = unavailable).
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
    /// <para>资源使用信息（电池/内存/存储）是否至少一项可用。</para>
    /// Whether at least one of battery/memory/storage resource metrics is available.
    /// </summary>
    public bool HasResourceInfo => HasSystemInfo && (HasBattery || HasMem || HasDisk);

    /// <summary>
    /// <para>磁盘占用文本（已用/总量）。</para>
    /// Disk usage text (used/total).
    /// </summary>
    public string DiskText { get; set => SetProperty(ref field, value); } = string.Empty;
}