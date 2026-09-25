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
    Adb,
    Hdc,
    Fastboot,
    Scrcpy,
    Edl,
    Mtk,
    Bootrom
}