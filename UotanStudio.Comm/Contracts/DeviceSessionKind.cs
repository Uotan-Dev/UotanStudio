namespace UotanStudio.Comm.Contracts;

/// <summary>
/// Protocol session the controller builds on top of the leased transport.
/// <para>控制器在已租用的精确传输之上构建的协议会话类型。</para>
/// The kind never changes the route: discovery and the transport opener decide the route,
/// and the matching factory only wraps the already-opened transport.
/// <para>会话类型不改变路由：路由由发现源与传输开启器决定，匹配的工厂只包装已打开的传输。</para>
/// </summary>
public enum DeviceSessionKind
{
    /// <summary>
    /// ADB session over an adb-server route or a direct USB interface.
    /// <para>经 adb-server 路由或直连 USB 接口的 ADB 会话。</para>
    /// </summary>
    Adb,
    /// <summary>
    /// HDC session over an hdc-server route or a direct USB interface.
    /// <para>经 hdc-server 路由或直连 USB 接口的 HDC 会话。</para>
    /// </summary>
    Hdc,
    /// <summary>
    /// Fastboot session on a direct USB flashing interface.
    /// <para>运行在直连 USB 刷写接口上的 fastboot 会话。</para>
    /// </summary>
    Fastboot,
    /// <summary>
    /// Qualcomm EDL session on a direct USB transport.
    /// <para>运行在直连 USB 传输上的高通 EDL 会话。</para>
    /// </summary>
    Edl,
    /// <summary>
    /// MTK boot-protocol session on a direct transport.
    /// <para>运行在直连传输上的 MTK boot 协议会话。</para>
    /// </summary>
    Mtk,
    /// <summary>
    /// Rockchip bootrom (RKDL) session on a direct USB transport.
    /// <para>运行在直连 USB 传输上的 Rockchip bootrom（RKDL）会话。</para>
    /// </summary>
    Bootrom
}