using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Implements the Rockchip RKDL (Rockchip Download) protocol over USB control and bulk transfers.
/// <para>在 USB 控制和批量传输之上实现 Rockchip RKDL 协议。</para>
/// Protocol flow:
/// 1. Download first-stage loader via vendor request 0x0C / wIndex 0x0471
/// 2. Device re-enumerates, download second-stage loader via vendor request 0x0C / wIndex 0x0472
/// 3. Device enters rockusb mode; use CBW/CSW bulk transfers for flash operations
/// 4. Commands: read/write LBA, write GPT, erase flash, reset
/// <para>协议流程：通过 vendor 请求 0x0C / wIndex 0x0471 下载第一阶段 loader →
/// 设备重新枚举 → 通过 vendor 请求 0x0C / wIndex 0x0472 下载第二阶段 loader →
/// 设备进入 rockusb 模式 → 使用 CBW/CSW 批量传输进行 flash 操作。</para>
/// </summary>
public sealed partial class RkdlSession : IProtocolSession
{
    private readonly int _timeoutMs;
    private readonly PhysicalDeviceIdentity? _physicalIdentity;
    private bool _disposed;

    /// <summary>
    /// Creates a new RKDL session over the specified transport.
    /// <para>在指定传输上创建新的 RKDL 会话。</para>
    /// </summary>
    /// <remarks>
    /// Bulk operations use <paramref name="transport"/>.Read/Write directly; vendor control
    /// transfers (loader download) require a <see cref="UsbDeviceTransport"/> and otherwise throw
    /// <see cref="NotSupportedException"/>.
    /// <para>批量操作直接使用 <paramref name="transport"/>.Read/Write；vendor 控制传输（loader
    /// 下载）需要 <see cref="UsbDeviceTransport"/>，否则抛出 <see cref="NotSupportedException"/>。</para>
    /// </remarks>
    /// <param name="transport">The underlying transport. <para>底层传输。</para></param>
    /// <param name="timeoutMs">Optional I/O timeout in milliseconds. <para>可选 I/O 超时（毫秒）。</para></param>
    internal RkdlSession(ICommTransport transport, int? timeoutMs = null)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeoutMs = timeoutMs ?? RkdlConstants.DefaultTimeoutMs;
        _physicalIdentity = UsbSession?.DeviceInfo is { } deviceInfo
            ? PhysicalDeviceIdentity.From(deviceInfo)
            : null;
        CommTrace.State("rkdl", $"Opened RKDL session: VID 0x{VendorId:X4} PID 0x{ProductId:X4}, stage {Mode}, " +
            $"bcdUSB 0x{UsbSession?.DeviceInfo.BcdUsb ?? 0:X4}, timeout {_timeoutMs} ms.");
    }

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    /// <value>The constant <c>"rkdl"</c> identifying the Rockchip download protocol.
    /// <para>常量 <c>"rkdl"</c>，标识 Rockchip 下载协议。</para></value>
    public string ProtocolName => "rkdl";

    /// <summary>
    /// Gets the underlying transport.
    /// <para>获取底层传输。</para>
    /// </summary>
    /// <value>The transport this session performs bulk transfers on; disposed together with the session.
    /// <para>本会话执行批量传输所使用的传输；随会话一同释放。</para></value>
    public ICommTransport Transport { get; }

    /// <summary>
    /// Gets whether the session is connected and ready for commands.
    /// <para>获取会话是否已连接并准备好接收命令。</para>
    /// </summary>
    /// <value><c>true</c> while the session is undisposed and its transport reports a connection.
    /// <para>会话未释放且其传输报告已连接时为 <c>true</c>。</para></value>
    public bool IsConnected => !_disposed && Transport.IsConnected;

    /// <summary>
    /// Gets the underlying USB device session, when the transport is USB-based
    /// (<c>null</c> otherwise). Needed for vendor control transfers such as the
    /// 0x0471/0x0472 loader download.
    /// <para>当传输基于 USB 时获取底层 USB 设备会话（否则为 <c>null</c>）。
    /// 0x0471/0x0472 loader 下载等 vendor 控制传输需要它。</para>
    /// </summary>
    internal IUsbDeviceSession? UsbSession => (Transport as UsbDeviceTransport)?.Session;

    /// <summary>Gets the USB vendor ID of the underlying device.
    /// <para>获取底层设备的 USB 厂商 ID。</para></summary>
    /// <value>The vendor ID read from the USB session, or 0 when the transport is not USB-based.
    /// <para>从 USB 会话读取的厂商 ID；传输非基于 USB 时为 0。</para></value>
    public ushort VendorId => UsbSession?.DeviceInfo.VendorId ?? 0;

    /// <summary>Gets the USB product ID of the underlying device.
    /// <para>获取底层设备的 USB 产品 ID。</para></summary>
    /// <value>The product ID read from the USB session, or 0 when the transport is not USB-based.
    /// <para>从 USB 会话读取的产品 ID；传输非基于 USB 时为 0。</para></value>
    public ushort ProductId => UsbSession?.DeviceInfo.ProductId ?? 0;

    /// <summary>Gets the stage this device reports: mask ROM, loader, or unknown when the descriptor carried no
    /// <c>bcdUSB</c> value.
    /// <para>获取设备报告所处阶段：mask ROM、loader，或描述符未携带 <c>bcdUSB</c> 取值时的未知。</para></summary>
    /// <value>The stage derived by <see cref="RkdlConstants.ClassifyMode"/> from the USB product ID and the
    /// <c>bcdUSB</c> value the session's device-info snapshot carries.
    /// <para>由 <see cref="RkdlConstants.ClassifyMode"/> 依据 USB 产品 ID 与本会话设备信息快照携带的
    /// <c>bcdUSB</c> 取值得出的阶段。</para></value>
    public RkdlDeviceMode Mode => RkdlConstants.ClassifyMode(ProductId, UsbSession?.DeviceInfo.BcdUsb);

    /// <summary>Gets whether the device is in loader mode, where CBW bulk commands such as chip-info and storage
    /// reads are valid.
    /// <para>获取设备是否处于 loader 模式，芯片信息读取与存储读取等 CBW 批量命令在该模式下有效。</para></summary>
    /// <value><c>true</c> when <see cref="Mode"/> is <see cref="RkdlDeviceMode.Loader"/>.
    /// <para><see cref="Mode"/> 为 <see cref="RkdlDeviceMode.Loader"/> 时为 <c>true</c>。</para></value>
    public bool IsRockusbMode => Mode == RkdlDeviceMode.Loader;

    /// <summary>Gets whether the device is still in mask ROM mode, the only stage that accepts the
    /// <c>0x0471</c>/<c>0x0472</c> loader-download vendor requests.
    /// <para>获取设备是否仍处于 Mask ROM 模式，该阶段是唯一接受 <c>0x0471</c>/<c>0x0472</c> loader 下载 vendor
    /// 请求的阶段。</para></summary>
    /// <value><c>true</c> when <see cref="Mode"/> is <see cref="RkdlDeviceMode.Maskrom"/>; a device whose stage is
    /// <see cref="RkdlDeviceMode.Unknown"/> reports <c>false</c> here and from <see cref="IsRockusbMode"/> alike.
    /// <para><see cref="Mode"/> 为 <see cref="RkdlDeviceMode.Maskrom"/> 时为 <c>true</c>；阶段为
    /// <see cref="RkdlDeviceMode.Unknown"/> 的设备在此与 <see cref="IsRockusbMode"/> 处均为 <c>false</c>。</para></value>
    public bool IsMaskromMode => Mode == RkdlDeviceMode.Maskrom;

    /// <summary>
    /// Checks whether this is a Rockchip boot ROM port in either stage: vendor <c>0x2207</c> with a product ID inside
    /// the boot ROM range (see <see cref="RkdlConstants.IsBootromProduct"/>).
    /// <para>检查这是否为任一阶段的瑞芯微 boot ROM 端口：厂商 <c>0x2207</c> 且产品 ID 位于 boot ROM 区间
    /// （见 <see cref="RkdlConstants.IsBootromProduct"/>）。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the vendor and product IDs match a Rockchip boot ROM port.
    /// <para>厂商与产品 ID 匹配瑞芯微 boot ROM 端口时返回 <see langword="true"/>。</para></returns>
    public bool IsRockchipDevice()
        => VendorId == RkdlConstants.RockchipVid && RkdlConstants.IsBootromProduct(ProductId);

    /// <summary>
    /// Returns the underlying USB device session for vendor control transfers, throwing
    /// <see cref="NotSupportedException"/> when the transport is not USB-based.
    /// <para>返回用于 vendor 控制传输的底层 USB 设备会话；当传输不是基于 USB 时抛出
    /// <see cref="NotSupportedException"/>。</para>
    /// </summary>
    private IUsbDeviceSession GetUsbSession()
    {
        return Transport is UsbDeviceTransport usb
            ? usb.Session
            : throw new NotSupportedException("RKDL loader download and vendor control transfers require a USB transport.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RkdlSession));
        }
    }

    /// <summary>
    /// Disposes the session and the underlying transport.
    /// <para>释放会话和底层传输。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CommTrace.State("rkdl", $"Disposing RKDL session: VID 0x{VendorId:X4} PID 0x{ProductId:X4}, stage {Mode}.");
        Transport.Dispose();
    }
}