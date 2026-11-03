namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Provides a complete USB backend contract: discovery, session enumeration, and capabilities.
/// <para>提供完整的 USB 后端契约：发现、会话枚举和能力描述。</para>
/// Implementations back a specific USB API (WinUSB, libusb, native, HarmonyOS) and
/// expose both metadata-only discovery and full session opening through this single contract.
/// <para>实现类支撑特定 USB API（WinUSB、libusb、原生、HarmonyOS），
/// 通过单一契约暴露仅元数据发现与会话打开能力。</para>
/// </summary>
internal interface IUsbApiProvider
{
    /// <summary>
    /// Gets the public API name.
    /// <para>获取对外公开的 API 名称。</para>
    /// </summary>
    string ApiName { get; }

    /// <summary>
    /// Gets the backend family.
    /// <para>获取后端类型。</para>
    /// </summary>
    UsbApiKind ApiKind { get; }

    /// <summary>
    /// Gets whether this backend is supported on the current platform.
    /// <para>获取当前平台是否支持该后端。</para>
    /// </summary>
    bool IsSupportedOnCurrentPlatform { get; }

    /// <summary>
    /// Enumerates device metadata using the optional filter without opening long-lived sessions.
    /// <para>使用可选过滤器枚举设备元数据，不建立长期会话。</para>
    /// </summary>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A list of matching device metadata. <para>匹配设备元数据列表。</para></returns>
    IReadOnlyList<UsbDeviceInfo> EnumerateDeviceInfos(UsbDeviceFilter? filter = null);

    /// <summary>
    /// Opens only the interface an exact-path filter names, skipping the device-tree walk a full
    /// session enumeration performs. Backends that cannot resolve a single path return
    /// <see langword="null"/>, and the caller then enumerates as before.
    /// <para>只打开精确路径过滤器所指的接口，跳过完整会话枚举所需的企业树遍历。无法解析单一路径的后端返回
    /// <see langword="null"/>，调用方随后照旧执行完整枚举。</para>
    /// </summary>
    /// <param name="filter">A filter carrying an exact device path. <para>携带精确设备路径的过滤器。</para></param>
    /// <returns>The opened session, or <see langword="null"/> when the backend has no single-path answer.
    /// <para>已打开的会话；后端无法按单一路径作答时为 <see langword="null"/>。</para></returns>
    IUsbDeviceSession? TryOpenExactSession(UsbDeviceFilter filter) => null;

    /// <summary>
    /// Enumerates device sessions using the optional filter.
    /// <para>使用可选过滤器枚举设备会话。</para>
    /// </summary>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A list of matching device sessions. <para>匹配设备会话列表。</para></returns>
    IReadOnlyList<IUsbDeviceSession> EnumerateDeviceSessions(UsbDeviceFilter? filter = null);

    /// <summary>
    /// Gets the capability profile of this provider.
    /// <para>获取该提供器的能力轮廓。</para>
    /// </summary>
    /// <returns>The provider capability profile. <para>提供器能力轮廓。</para></returns>
    UsbApiCapabilities GetCapabilities();
}