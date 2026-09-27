using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Comm.Protocols.Mtk.XmlProfile;

/// <summary>
/// The handoff produced after an XML DA reboot was acknowledged: the old session is terminal and
/// the caller must re-establish communication through the device layer instead of this session.
/// <para>XML DA 重启被确认后产出的交接信息：旧会话已终结，调用方必须经由设备层重新建立通信，
/// 而不是继续使用本会话。</para>
/// The offer never replays commands, never reconnects by itself, and never claims the device
/// reached a new boot mode; it only states which physical identity and transport generation were
/// lost and how long the caller should allow the device layer to observe the replacement
/// endpoint. Reconnection is performed by the host through <c>DeviceController</c> (for example
/// <c>WaitForUsbReconnectAsync</c>), reusing the mode-transition machinery common to all protocols.
/// <para>本交接绝不重放命令、绝不自行重连，也绝不声称设备已进入新启动模式；它只说明丢失了哪个物理身份
/// 与传输代际，以及调用方应允许设备层等待替换端点出现的时长。重连由宿主经 <c>DeviceController</c>
/// （例如 <c>WaitForUsbReconnectAsync</c>）完成，复用所有协议统一的模式迁移机制。</para>
/// </summary>
public sealed record MtkRebootTransitionOffer
{
    /// <summary>
    /// Gets the physical identity observed when the session was opened, or <see langword="null"/>
    /// when the transport exposed no correlatable identity (for example a serial port or an
    /// adopted transport without descriptor metadata).
    /// <para>获取会话打开时观测到的物理身份；传输未暴露可关联身份时（例如串口或无描述符元数据的
    /// 被接管传输）为 <see langword="null"/>。</para>
    /// </summary>
    public required PhysicalDeviceIdentity? Identity { get; init; }

    /// <summary>
    /// Gets the transport generation that the rebooted session consumed, or <see langword="null"/>
    /// when the session was not bound to a tracked generation.
    /// <para>获取本次重启所消耗的传输代际；会话未绑定被跟踪代际时为 <see langword="null"/>。</para>
    /// </summary>
    public TransportGeneration? Generation { get; init; }

    /// <summary>
    /// Gets the remaining budget, in milliseconds, the caller should grant the device layer to
    /// observe the replacement endpoint before treating the reboot as unresolved.
    /// <para>获取调用方应授予设备层观测替换端点的剩余预算（毫秒）；超过后须将重启判定为未解决。</para>
    /// </summary>
    public required int ReconnectTimeoutMs { get; init; }

    /// <summary>
    /// Gets the storage generation the session had published before the reboot cleared it.
    /// <para>获取重启清除前会话已发布的存储代际。</para>
    /// </summary>
    /// <value>The last storage generation counter, or <c>0</c> when no geometry was ever published.
    /// <para>最近的存储代际计数；从未发布几何信息时为 <c>0</c>。</para></value>
    public ulong StorageGenerationAtReboot { get; init; }
}