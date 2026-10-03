using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// <para>只读 EDL 探测附加采集到的设备详情快照；未采集到的部分为 null。</para>
/// A snapshot of device details collected as a side effect of the read-only EDL probe;
/// parts that were not collected stay null.
/// </summary>
public sealed record EdlDetailsSnapshot
{
    /// <summary>
    /// <para>Sahara HELLO 上报的协议版本与最大命令长度（Sahara 模式设备）。</para>
    /// The Sahara HELLO-reported protocol version and maximum command length (Sahara-mode devices).
    /// </summary>
    public SaharaHandshakeInfo? Sahara { get; init; }

    /// <summary>
    /// <para>端点走 QDLoader 串口驱动回退时的 COM 端口名；原生 USB 传输时为 null。</para>
    /// The COM port name when the endpoint uses the QDLoader serial-driver fallback;
    /// null over a native USB transport.
    /// </summary>
    public string? SerialPortName { get; init; }

    /// <summary>
    /// <para>Firehose configure 协商出的能力快照（Firehose 模式设备）。</para>
    /// The capability snapshot negotiated by Firehose configure (Firehose-mode devices).
    /// </summary>
    public FirehoseCapabilitySnapshot? Capabilities { get; init; }

    /// <summary>
    /// <para>getstorageinfo 上报的存储几何信息（块大小与总块数）。</para>
    /// The storage geometry reported by getstorageinfo (block size and total blocks).
    /// </summary>
    public FirehoseStorageGeometry? Geometry { get; init; }

    /// <summary>
    /// <para>getstorageinfo 日志合并出的描述性存储信息（类型、序列号、固件等）。</para>
    /// Descriptive storage facts merged from getstorageinfo logs (type, serial, firmware, ...).
    /// </summary>
    public FirehoseStorageDetails? Storage { get; init; }
}