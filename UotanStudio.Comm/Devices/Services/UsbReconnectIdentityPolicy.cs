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
    PhysicalPort,
    /// <summary>Accepts the Rockchip loader-stage replacement of a maskrom port: the same physical
    /// location (backend port path or the Windows hub chain behind the device instance id) and loader-stage
    /// evidence (<c>bcdUSB</c> bit 0 set or the vendor's <c>ROCKCHIP</c> loader serial), while the container ID
    /// may legitimately change because the stage switch rewrites the reported identity.
    /// <para>接受 maskrom 端口的瑞芯微 loader 阶段替代端点：相同物理位置（后端端口路径或设备实例 ID
    /// 背后的 Windows 集线器链）加 loader 阶段证据（<c>bcdUSB</c> bit 0 置位或厂商的 <c>ROCKCHIP</c>
    /// loader 序列号），同时允许容器 ID 变化——阶段切换本就会改写上报的身份。</para></summary>
    RockchipLoader,
    /// <summary>Accepts a replacement at the same physical location (the Windows hub chain behind the device
    /// instance id, or the backend port path) when the boot stage rewrites the reported identity: the container ID
    /// and the serial may both change, and neither is compared. This authorizes matching a connection position, not
    /// proof that the same unit returned, so it is only for a transition inside one device the caller is already
    /// driving.
    /// <para>当引导阶段改写上报身份时，接受同一物理位置（设备实例 ID 背后的 Windows 集线器链，或后端端口路径）上的
    /// 替代端点：容器 ID 与序列号都可能变化，且两者都不参与比较。此策略显式授权匹配连接位置，不证明返回的是同一台
    /// 设备，因此只用于调用方正在驱动的同一台设备内部的阶段切换。</para></summary>
    PhysicalLocation
}