using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Factory for creating <see cref="FastbootSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="FastbootSession"/> 实例的工厂。</para>
/// By default the factory matches strictly on the standard fastboot USB interface
/// (class 0xFF, subclass 0x42, protocol 0x03) and rejects devices whose interface metadata
/// is unavailable, so unrelated devices (EDL, Rockchip loaders, ...) are never claimed.
/// Set <see cref="MatchMode"/> to <see cref="FastbootMatchMode.InterfaceOrKnownVidPid"/>
/// to additionally accept devices listed in <see cref="KnownDeviceProfiles"/> by exact
/// VID+PID pair (e.g. legacy Amazon Kindle bootloaders with non-standard descriptors).
/// <para>默认情况下，工厂严格按标准 fastboot USB 接口（class 0xFF、subclass 0x42、
/// protocol 0x03）匹配，并拒绝接口元数据不可用的设备，因此不会误认无关设备
/// （EDL、Rockchip loader 等）。将 <see cref="MatchMode"/> 设为
/// <see cref="FastbootMatchMode.InterfaceOrKnownVidPid"/> 可额外按精确 VID+PID 对接受
/// <see cref="KnownDeviceProfiles"/> 中列出的设备（如接口描述符非标准的老款
/// Amazon Kindle bootloader）。</para>
/// </summary>
internal sealed class FastbootFactory : IProtocolFactory
{
    private readonly ushort? _vendorId;
    private readonly ushort? _productId;
    private readonly int? _timeoutMs;

    /// <summary>Creates a new fastboot factory that matches any standard fastboot interface.
    /// <para>创建匹配任何标准 fastboot 接口的新工厂。</para></summary>
    public FastbootFactory() { }

    /// <summary>Creates a new fastboot factory that matches a specific VID/PID.
    /// <para>创建匹配指定 VID/PID 的新工厂。</para></summary>
    public FastbootFactory(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>Creates a new fastboot factory with a specific I/O timeout.
    /// <para>创建具有特定 I/O 超时的新工厂。</para></summary>
    public FastbootFactory(int timeoutMs)
    {
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Gets or sets the USB device matching mode. Defaults to
    /// <see cref="FastbootMatchMode.InterfaceOnly"/> (standard fastboot interface only).
    /// <para>获取或设置 USB 设备匹配模式。默认为
    /// <see cref="FastbootMatchMode.InterfaceOnly"/>（仅标准 fastboot 接口）。</para>
    /// </summary>
    public FastbootMatchMode MatchMode { get; set; } = FastbootMatchMode.InterfaceOnly;

    /// <summary>
    /// Gets or sets the known fastboot device profiles (exact VID/PID pairs) accepted as a
    /// fallback when <see cref="MatchMode"/> is
    /// <see cref="FastbootMatchMode.InterfaceOrKnownVidPid"/>.
    /// <para>获取或设置已知 fastboot 设备档案（精确 VID/PID 对），当
    /// <see cref="MatchMode"/> 为 <see cref="FastbootMatchMode.InterfaceOrKnownVidPid"/>
    /// 时用作匹配兜底。</para>
    /// </summary>
    public IReadOnlyList<FastbootDeviceProfile> KnownDeviceProfiles { get; set; } = FastbootDeviceProfiles.Default;

    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "fastboot";

    /// <summary>
    /// Tries to create a fastboot session over the specified USB transport session.
    /// <para>尝试在指定 USB 传输会话上创建 fastboot 会话。</para>
    /// </summary>
    public bool TryCreate(IUsbDeviceSession transport, out IProtocolSession? session)
    {
        session = null;
        try
        {
            var info = transport.DeviceInfo;

            if (_vendorId.HasValue && info.VendorId != _vendorId.Value)
            {
                return false;
            }

            if (_productId.HasValue && info.ProductId != _productId.Value)
            {
                return false;
            }

            var hasFastbootInterface = HasStandardFastbootInterface(info);
            if (!hasFastbootInterface)
            {
                if (MatchMode != FastbootMatchMode.InterfaceOrKnownVidPid)
                {
                    return false;
                }

                if (!MatchesKnownProfile(info))
                {
                    return false;
                }
            }

            session = new FastbootSession(new UsbDeviceTransport(transport), _timeoutMs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the device exposes the standard fastboot interface, either via an
    /// observed interface descriptor or via the device-level interface metadata (which the
    /// backend may have inferred from the enumeration filter). Devices whose interface
    /// metadata is entirely unavailable never match.
    /// <para>检查设备是否暴露标准 fastboot 接口：依据观测到的接口描述符，或设备级接口元数据
    /// （后端可能由枚举过滤条件推断得到）。接口元数据完全不可用的设备绝不匹配。</para>
    /// </summary>
    private static bool HasStandardFastbootInterface(UsbDeviceInfo info)
    {
        foreach (var iface in info.Interfaces)
        {
            if (iface.Class == FastbootConstants.FastbootInterfaceClass &&
                iface.SubClass == FastbootConstants.FastbootInterfaceSubclass &&
                iface.Protocol == FastbootConstants.FastbootInterfaceProtocol)
            {
                return true;
            }
        }

        return info.InterfaceClass == FastbootConstants.FastbootInterfaceClass &&
               info.InterfaceSubClass == FastbootConstants.FastbootInterfaceSubclass &&
               info.InterfaceProtocol == FastbootConstants.FastbootInterfaceProtocol;
    }

    /// <summary>
    /// Checks whether the device matches any known device profile by exact VID+PID pair.
    /// <para>检查设备是否按精确 VID+PID 对匹配任一已知设备档案。</para>
    /// </summary>
    private bool MatchesKnownProfile(UsbDeviceInfo info)
    {
        foreach (var profile in KnownDeviceProfiles)
        {
            if (profile.Matches(info.VendorId, info.ProductId))
            {
                return true;
            }
        }
        return false;
    }
}