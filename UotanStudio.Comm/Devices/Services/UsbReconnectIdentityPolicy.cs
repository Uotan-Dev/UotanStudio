namespace UotanStudio.Comm.Devices.Services;

/// <summary>Selects the evidence required to correlate a USB endpoint across one observed disappearance.
/// <para>选择在一次已观测消失前后关联 USB 端点所需的证据。</para></summary>
public enum UsbReconnectIdentityPolicy
{
    /// <summary>Requires the same nonempty operating-system container ID.
    /// <para>要求相同的非空操作系统容器 ID。</para></summary>
    Container,
    /// <summary>Requires the same backend-observed physical port chain and rejects conflicting container IDs.
    /// This explicitly authorizes matching a connection position, not proof that the same unit returned.
    /// <para>要求相同的后端观测物理端口链，且拒绝冲突的容器 ID。
    /// 此策略显式授权匹配连接位置，不证明返回的是同一台设备。</para></summary>
    PhysicalPort
}