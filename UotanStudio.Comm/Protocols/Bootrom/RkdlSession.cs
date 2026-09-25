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

    /// <summary>Gets whether the device is in rockusb mode (ready for flash operations).
    /// <para>设备是否处于 rockusb 模式（就绪可刷写）。</para></summary>
    /// <value><c>true</c> when the device is a Rockchip device whose product ID matches a known
    /// loader-mode ID. <para>设备为 Rockchip 且产品 ID 命中已知 loader 模式 ID 时为 <c>true</c>。</para></value>
    public bool IsRockusbMode => IsRockchipDevice() && RkdlConstants.KnownLoaderProductIds.Contains(ProductId);

    /// <summary>
    /// Gets whether the device is still in mask ROM (bootrom) mode, i.e. a Rockchip device
    /// whose product ID is not one of the known loader-mode IDs. Only maskrom devices accept
    /// the 0x0471/0x0472 loader-download vendor requests.
    /// <para>获取设备是否仍处于 Mask ROM（bootrom）模式，即产品 ID 不在已知 loader 模式 ID
    /// 之列的 Rockchip 设备。只有 maskrom 设备接受 0x0471/0x0472 loader 下载 vendor 请求。</para>
    /// </summary>
    /// <value><c>true</c> for a Rockchip device whose product ID is not a known loader-mode ID.
    /// <para>设备为 Rockchip 且产品 ID 不属于已知 loader 模式 ID 时为 <c>true</c>。</para></value>
    public bool IsMaskromMode => IsRockchipDevice() && !IsRockusbMode;

    /// <summary>
    /// Checks if this is a Rockchip bootrom device (maskrom or loader mode). Uses the upstream
    /// generic rule (vendor 0x2207 and pid >> 8 != 0) plus the known loader product IDs
    /// (see rkdeveloptool RKScan.cpp IsRockusbDevice).
    /// <para>检查是否为 Rockchip bootrom 设备（maskrom 或 loader 模式）。使用上游通用规则
    /// （厂商 0x2207 且 pid >> 8 != 0）加已知 loader 产品 ID（见 rkdeveloptool RKScan.cpp
    /// 的 IsRockusbDevice）。</para>
    /// </summary>
    /// <returns><c>true</c> when the vendor and product IDs match the Rockchip bootrom or a
    /// known loader product ID. <para>厂商与产品 ID 匹配 Rockchip bootrom 或已知 loader
    /// 产品 ID 时返回 <c>true</c>。</para></returns>
    public bool IsRockchipDevice()
        => VendorId == RkdlConstants.RockchipVid && ((ProductId >> 8) != 0 || RkdlConstants.KnownLoaderProductIds.Contains(ProductId));

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
        Transport.Dispose();
    }
}