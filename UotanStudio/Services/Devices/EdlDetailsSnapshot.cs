using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// A snapshot of device details collected as a side effect of the read-only EDL probe;
/// parts that were not collected stay null.
/// <para>只读 EDL 探测附加采集到的设备详情快照；未采集到的部分为 null。</para>
/// </summary>
public sealed record EdlDetailsSnapshot
{
    /// <summary>
    /// The Sahara HELLO-reported protocol version and maximum command length for Sahara-mode devices.
    /// <para>Sahara 模式设备由 HELLO 上报的协议版本与最大命令长度。</para>
    /// </summary>
    /// <value>The handshake info, or <c>null</c> when no HELLO was captured.<para>握手信息；未捕获 HELLO 时为 <c>null</c>。</para></value>
    public SaharaHandshakeInfo? Sahara { get; init; }

    /// <summary>
    /// The chip identity read passively during the Sahara handshake: hardware id, ChipIdentifierV3, serial number
    /// and OEM public-key hash. Null when it was never read.
    /// <para>Sahara 握手期间被动读到的芯片身份：硬件编号、ChipIdentifierV3、序列号与 OEM 公钥哈希。未读到或未查询时为 null。</para>
    /// </summary>
    /// <value>The device identity, or <c>null</c> when the handshake produced none.<para>设备身份；握手未产生时为 <c>null</c>。</para></value>
    public SaharaDeviceIdentity? Identity { get; init; }

    /// <summary>
    /// The COM port name when the endpoint uses the QDLoader serial-driver fallback;
    /// null over a native USB transport.
    /// <para>端点走 QDLoader 串口驱动回退时的 COM 端口名；原生 USB 传输时为 null。</para>
    /// </summary>
    /// <value>The serial port name, or <c>null</c> over a native USB transport.<para>串口端口名；原生 USB 传输时为 <c>null</c>。</para></value>
    public string? SerialPortName { get; init; }

    /// <summary>
    /// The capability snapshot negotiated by Firehose configure for Firehose-mode devices.
    /// <para>Firehose 模式设备由 configure 协商出的能力快照。</para>
    /// </summary>
    /// <value>The negotiated capabilities, or <c>null</c> when configure did not report them.<para>协商出的能力；configure 未上报时为 <c>null</c>。</para></value>
    public FirehoseCapabilitySnapshot? Capabilities { get; init; }

    /// <summary>
    /// The storage geometry reported by getstorageinfo (block size and total blocks).
    /// <para>getstorageinfo 上报的存储几何信息（块大小与总块数）。</para>
    /// </summary>
    /// <value>The reported geometry, or <c>null</c> when no storage info was received.<para>上报的几何信息；未收到存储信息时为 <c>null</c>。</para></value>
    public FirehoseStorageGeometry? Geometry { get; init; }

    /// <summary>
    /// Descriptive storage facts merged from getstorageinfo logs, such as type, serial and firmware fields.
    /// <para>由 getstorageinfo 日志合并出的描述性存储信息，例如类型、序列号与固件字段。</para>
    /// </summary>
    /// <value>The merged storage details, or <c>null</c> when the logs yielded none.<para>合并后的存储详情；日志未产出时为 <c>null</c>。</para></value>
    public FirehoseStorageDetails? Storage { get; init; }
}