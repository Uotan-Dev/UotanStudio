using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// The evidence an RKDL session publishes around a loader download so a caller can ask the device
/// layer to wait for the post-download USB re-enumeration and open the replacement endpoint of the
/// same physical device. Completion of the settle window is not evidence that the device reached
/// loader mode: <see cref="RkdlSession.IsRockusbMode"/> derives from a device-info snapshot taken
/// when the session was constructed and can never observe the transition inside the session.
/// <para>RKDL 会话在 loader 下载前后发布的证据，供调用方请求设备层等待下载完成后的 USB 重枚举，
/// 并为同一物理设备打开替代端点。稳定窗口的结束并不能证明设备已进入 loader 模式：
/// <see cref="RkdlSession.IsRockusbMode"/> 派生自会话构造时拍摄的设备信息快照，在会话内永远观测不到
/// 该模式变化。</para>
/// A loader download also switches the USB product ID (maskrom <c>0x350B</c> versus
/// <see cref="RkdlConstants.KnownLoaderProductIds"/>), while the controller's USB reconnection path
/// requires the previous endpoint and the replacement to report equal vendor and product IDs. Whether
/// a PID-switching bootrom transition passes that validation needs real-device confirmation; this
/// repository has not verified it on hardware. The offer itself never reconnects, never replays a
/// command and never claims a mode change; a session that was not leased through the device layer
/// publishes <see langword="null"/> for <see cref="PreviousEndpoint"/> and <see cref="ObservedGeneration"/>,
/// which the caller completes with <c>with</c> expressions before handing the offer to the controller.
/// <para>loader 下载还会切换 USB 产品 ID（maskrom <c>0x350B</c> 与
/// <see cref="RkdlConstants.KnownLoaderProductIds"/> 之列的 loader ID），而控制器的 USB 重连路径要求
/// 旧端点与替代端点上报相同的厂商与产品 ID。切换 PID 的 bootrom 迁移能否通过该校验需真机确认，
/// 本仓库未在硬件上验证。提议本身绝不重连、绝不重放命令、绝不声称模式已切换；未经设备层租用的会话
/// 会把 <see cref="PreviousEndpoint"/> 与 <see cref="ObservedGeneration"/> 发布为 <see langword="null"/>，
/// 由调用方用 <c>with</c> 表达式补全后再交给控制器。</para>
/// </summary>
public sealed record RkdlLoaderTransitionOffer
{
    /// <summary>
    /// Gets the endpoint the loader-download session was leased on, when the session knows one.
    /// <para>获取 loader 下载会话所租用的端点（若会话可知）。</para>
    /// </summary>
    /// <value>The exact previous endpoint, or <see langword="null"/> when the session had none and
    /// the caller must complete the offer. <para>精确的旧端点；会话没有端点时为 <see langword="null"/>，
    /// 需由调用方补全提议。</para></value>
    public required DeviceEndpoint? PreviousEndpoint { get; init; }

    /// <summary>
    /// Gets the best-evidence physical identity of the device behind the previous endpoint, captured
    /// when the session was opened.
    /// <para>获取会话打开时捕获的旧端点背后设备的最优物理身份。</para>
    /// </summary>
    /// <value>The correlated identity, or <see langword="null"/> when the transport exposed no
    /// correlatable metadata (for example a non-USB transport or a device without container or
    /// usable serial evidence). <para>可关联的身份；传输未暴露可关联元数据时（例如非 USB 传输，或既无
    /// 容器 ID 也无可用序列号证据的设备）为 <see langword="null"/>。</para></value>
    public PhysicalDeviceIdentity? Identity { get; init; }

    /// <summary>
    /// Gets the transport generation the previous endpoint was opened on, when the controller stamped one.
    /// <para>获取旧端点打开所在的传输代际（若控制器盖印过）。</para>
    /// </summary>
    /// <value>The observed generation, or <see langword="null"/>. <para>观测到的代际，或 <see langword="null"/>。</para></value>
    public TransportGeneration? ObservedGeneration { get; init; }

    /// <summary>
    /// Gets the recommended cooperative deadline for waiting for the replacement endpoint.
    /// <para>获取等待替代端点建议采用的协作式截止时间。</para>
    /// </summary>
    /// <value>Defaults to <see cref="RkdlConstants.LoaderRelinkTimeoutMs"/>, matching the controller
    /// bound of 1 millisecond through 24 hours.
    /// <para>默认取 <see cref="RkdlConstants.LoaderRelinkTimeoutMs"/>，与控制器 1 毫秒至 24 小时的约束一致。</para></value>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(RkdlConstants.LoaderRelinkTimeoutMs);
}