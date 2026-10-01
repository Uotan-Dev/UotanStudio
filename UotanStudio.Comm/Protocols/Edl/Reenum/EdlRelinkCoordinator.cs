using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Comm.Protocols.Edl.Reenum;

/// <summary>
/// Waits for the post-programmer USB re-enumeration described by an <see cref="EdlRelinkOffer"/> and
/// opens the uniquely correlated replacement endpoint through the device controller.
/// <para>等待 <see cref="EdlRelinkOffer"/> 描述的写入 programmer 后的 USB 重枚举，
/// 并经设备控制器打开唯一关联的替代端点。</para>
/// The coordinator selects the correlation policy from the offer identity: a container id wins,
/// otherwise a usable physical port path; without either it refuses with <see cref="NotSupportedException"/>
/// instead of guessing from VID/PID. The caller must have released the previous lease and is responsible
/// for disposing the stale EDL session; this type never sends protocol bytes and never attaches a
/// transport to an existing session — the replacement lease carries a fresh session opened by the
/// registered factory.
/// <para>协调器依据提议身份选择关联策略：容器 ID 优先，否则使用可用的物理端口路径；
/// 两者皆缺时以 <see cref="NotSupportedException"/> 拒绝而非按 VID/PID 猜测。调用方必须已释放旧租约，
/// 并负责释放失效的 EDL 会话；本类型从不发送协议字节，也不把传输挂到既有会话上——
/// 替代租约携带由已注册工厂全新打开的会话。</para>
/// </summary>
public sealed class EdlRelinkCoordinator
{
    /// <summary>
    /// Gets the shared stateless instance.
    /// <para>获取无状态的共享实例。</para>
    /// </summary>
    public static EdlRelinkCoordinator Instance { get; } = new();

    /// <summary>
    /// Waits for and leases the replacement endpoint of the same physical device.
    /// <para>等待并租用同一物理设备的替代端点。</para>
    /// </summary>
    /// <param name="controller">
    /// The controller that owned the previous endpoint. <para>曾持有旧端点的控制器。</para>
    /// </param>
    /// <param name="offer">
    /// The relink offer, whose <see cref="EdlRelinkOffer.PreviousEndpoint"/> must be completed.
    /// <para>重链提议，其 <see cref="EdlRelinkOffer.PreviousEndpoint"/> 必须已补全。</para>
    /// </param>
    /// <param name="expectedKind">
    /// The explicitly expected session kind for the replacement endpoint.
    /// <para>替代端点显式预期的会话类型。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for discovery, waiting and opening. <para>发现、等待与打开的取消令牌。</para>
    /// </param>
    /// <returns>
    /// A caller-owned lease for the uniquely verified replacement endpoint.
    /// <para>由调用方持有的唯一经校验替代端点租约。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controller"/> or <paramref name="offer"/> is <see langword="null"/>,
    /// or the offer lacks its previous endpoint.
    /// <para><paramref name="controller"/> 或 <paramref name="offer"/> 为 <see langword="null"/>，
    /// 或提议缺少旧端点。</para></exception>
    /// <exception cref="NotSupportedException">
    /// The offer identity carries neither container nor usable port evidence, or the kind is unsupported.
    /// <para>提议身份既无容器也无可用端口证据，或不支持该会话类型。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The offer timeout is outside 1 millisecond through 24 hours.
    /// <para>提议截止时间超出 1 毫秒至 24 小时范围。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The previous lease is still active or several endpoints match the evidence.
    /// <para>旧租约仍活动，或多个端点与证据匹配。</para></exception>
    /// <exception cref="TimeoutException">
    /// The deadline expired without a verified replacement.
    /// <para>截止时间已到，未能得到经校验的替代端点。</para></exception>
    public async Task<DeviceLease> RelinkAsync(DeviceController controller, EdlRelinkOffer offer,
        DeviceSessionKind expectedKind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(offer);
        if (offer.PreviousEndpoint is not { } previous)
        {
            throw new ArgumentNullException(nameof(offer),
                "The relink offer must carry the previous endpoint before relinking; complete it with the leased endpoint.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(offer.Timeout, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offer.Timeout, TimeSpan.FromHours(24));
        var policy = SelectPolicy(offer);
        return await controller.WaitForUsbReconnectAsync(previous, expectedKind, policy,
            (int)Math.Ceiling(offer.Timeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
    }

    private static UsbReconnectIdentityPolicy SelectPolicy(EdlRelinkOffer offer)
    {
        if (offer.Identity is not { } identity)
        {
            throw new NotSupportedException(
                "The relink offer carries no physical device identity; correlation would fall back to VID/PID or list order.");
        }

        if (identity.ContainerId is { } container && container != Guid.Empty)
        {
            return UsbReconnectIdentityPolicy.Container;
        }

        if (identity.PhysicalPortPath is { Length: > 0 and <= 1024 } port && port.All(static value => !char.IsWhiteSpace(value) && !char.IsControl(value)))
        {
            return UsbReconnectIdentityPolicy.PhysicalPort;
        }

        throw new NotSupportedException(
            "The relink offer lacks both container and usable physical-port evidence required for re-enumeration correlation.");
    }
}