namespace UotanStudio.Mirroring;

/// <summary>
/// Wire constants of the ohscrcpy device-server protocol (upstream <c>OHScrcpyServer.hap</c>).
/// <para>ohscrcpy 设备端服务协议（上游 <c>OHScrcpyServer.hap</c>）的线协议常量。</para>
/// </summary>
/// <remarks>
/// <para>
/// ohscrcpy is not scrcpy-protocol compatible. One loopback TCP connection carries every packet as a
/// big-endian <c>type (4)</c> + <c>length (4)</c> header followed by the payload; video configuration,
/// H.264 access units, control messages, heartbeats and device status share that single connection.
/// These constants are tied to the published HAP and must move with it.
/// </para>
/// <para>
/// ohscrcpy 与 scrcpy 协议不兼容。单条回环 TCP 连接上每个包都是大端 <c>type (4)</c> + <c>length (4)</c>
/// 头加负载；视频配置、H.264 访问单元、控制消息、心跳与设备状态共用这一条连接。以下常量与已发布的
/// HAP 绑定，必须与其一起更新。
/// </para>
/// </remarks>
public static class OhscrcpyConstants
{
    /// <summary>Bundle name of the device-server application. <para>设备端服务应用的包名。</para></summary>
    public const string BundleName = "com.ohos.scrcpy.server";

    /// <summary>ServiceExtensionAbility that owns the TCP server and the capture pipeline.
    /// <para>持有 TCP 服务与采集管线的 ServiceExtensionAbility。</para></summary>
    public const string ServiceAbilityName = "ScrcpyService";

    /// <summary>UIAbility the service starts only to request screen-capture authorization on API below 15.
    /// <para>服务仅在 API 低于 15 时启动的、用于请求截屏授权的 UIAbility。</para></summary>
    public const string EntryAbilityName = "EntryAbility";

    /// <summary>Device loopback TCP port the service listens on. <para>服务在设备上监听的回环 TCP 端口。</para></summary>
    public const int DevicePort = 53535;

    /// <summary>Byte length of the packet header: type plus payload length. <para>包头字节长度：类型加负载长度。</para></summary>
    public const int HeaderLength = 8;

    /// <summary>Upper bound for one packet payload, 32 MiB. <para>单个包负载上限，32 MiB。</para></summary>
    public const int MaxPacketLength = 32 * 1024 * 1024;

    /// <summary>Heartbeat interval used to stay below the server's 30-second idle timeout.
    /// <para>用于低于服务端 30 秒空闲超时的心跳间隔。</para></summary>
    public const int HeartbeatIntervalMs = 10_000;

    /// <summary>Server-side idle timeout after which a silent client is disconnected.
    /// <para>服务端在客户端静默多久后断开连接的空闲超时。</para></summary>
    public const int ServerIdleTimeoutMs = 30_000;

    /// <summary>Packet type: heartbeat, echoed verbatim by the server. <para>包类型：心跳，服务端原样回显。</para></summary>
    public const uint PacketTypeHeartbeat = 0x01;

    /// <summary>Packet type: video stream configuration. <para>包类型：视频流配置。</para></summary>
    public const uint PacketTypeVideoConfig = 0x02;

    /// <summary>Packet type: one video frame. <para>包类型：一个视频帧。</para></summary>
    public const uint PacketTypeVideoFrame = 0x03;

    /// <summary>Packet type: control message, client to server. <para>包类型：控制消息，客户端到服务端。</para></summary>
    public const uint PacketTypeControl = 0x10;

    /// <summary>Packet type: device status, server to client. <para>包类型：设备状态，服务端到客户端。</para></summary>
    public const uint PacketTypeDeviceStatus = 0x20;

    /// <summary>Video codec identifier: H.264 Annex-B access units. <para>视频编码标识：H.264 Annex-B 访问单元。</para></summary>
    public const byte VideoCodecH264 = 0;

    /// <summary>Video codec identifier: raw RGBA frames. <para>视频编码标识：原始 RGBA 帧。</para></summary>
    public const byte VideoCodecRawRgba = 1;

    /// <summary>Video codec identifier: JPEG frames. <para>视频编码标识：JPEG 帧。</para></summary>
    public const byte VideoCodecJpeg = 2;

    /// <summary>Control sub-type: touch down. <para>控制子类型：触摸按下。</para></summary>
    public const byte ControlTouchDown = 0x01;

    /// <summary>Control sub-type: touch move. <para>控制子类型：触摸移动。</para></summary>
    public const byte ControlTouchMove = 0x02;

    /// <summary>Control sub-type: touch up. <para>控制子类型：触摸抬起。</para></summary>
    public const byte ControlTouchUp = 0x03;

    /// <summary>Control sub-type: mouse event, including vertical scroll. <para>控制子类型：鼠标事件，含纵向滚动。</para></summary>
    public const byte ControlMouseEvent = 0x04;

    /// <summary>Control sub-type: key code event. <para>控制子类型：键码事件。</para></summary>
    public const byte ControlKeyEvent = 0x10;

    /// <summary>Control sub-type: power key press. <para>控制子类型：电源键按压。</para></summary>
    public const byte ControlPowerKey = 0x11;

    /// <summary>Control sub-type: home key press. <para>控制子类型：主页键按压。</para></summary>
    public const byte ControlHomeKey = 0x12;

    /// <summary>Control sub-type: back key press. <para>控制子类型：返回键按压。</para></summary>
    public const byte ControlBackKey = 0x13;

    /// <summary>Control sub-type: UTF-8 text input. <para>控制子类型：UTF-8 文本输入。</para></summary>
    public const byte ControlTextInput = 0x15;

    /// <summary>Control sub-type: volume up. <para>控制子类型：音量加。</para></summary>
    public const byte ControlVolumeUp = 0x20;

    /// <summary>Control sub-type: volume down. <para>控制子类型：音量减。</para></summary>
    public const byte ControlVolumeDown = 0x21;

    /// <summary>Control sub-type: brightness up. <para>控制子类型：亮度加。</para></summary>
    public const byte ControlBrightnessUp = 0x22;

    /// <summary>Control sub-type: brightness down. <para>控制子类型：亮度减。</para></summary>
    public const byte ControlBrightnessDown = 0x23;

    /// <summary>Control sub-type: list installable applications. <para>控制子类型：列出可管理的应用。</para></summary>
    public const byte ControlListApps = 0x30;

    /// <summary>Control sub-type: pause encoder output (back-pressure). <para>控制子类型：暂停编码输出（背压）。</para></summary>
    public const byte ControlPauseEncoder = 0x40;

    /// <summary>Control sub-type: resume encoder output. <para>控制子类型：恢复编码输出。</para></summary>
    public const byte ControlResumeEncoder = 0x41;

    /// <summary>Control sub-type: change resolution, bitrate and frame rate.
    /// <para>控制子类型：改变分辨率、码率与帧率。</para></summary>
    public const byte ControlChangeVideoParams = 0x42;

    /// <summary>Control sub-type: rebuild the H.264 encoder while keeping the capture session.
    /// <para>控制子类型：在保留采集会话的前提下重建 H.264 编码器。</para></summary>
    public const byte ControlRestartEncoder = 0x43;

    /// <summary>Device-status sub-type: display geometry. <para>设备状态子类型：显示几何信息。</para></summary>
    public const byte DeviceStatusDisplayInfo = 0x01;

    /// <summary>Device-status sub-type: application list. <para>设备状态子类型：应用列表。</para></summary>
    public const byte DeviceStatusAppList = 0x10;

    /// <summary>Mouse action value for a scroll-axis update, aligned with OpenHarmony <c>MouseEvent.Action</c>.
    /// <para>滚轮轴更新对应的鼠标动作值，与 OpenHarmony <c>MouseEvent.Action</c> 对齐。</para></summary>
    public const byte MouseActionAxisUpdate = 5;

    /// <summary>Vertical scroll axis identifier, aligned with OpenHarmony <c>Axis.SCROLL_VERTICAL</c>.
    /// <para>纵向滚轮轴标识，与 OpenHarmony <c>Axis.SCROLL_VERTICAL</c> 对齐。</para></summary>
    public const sbyte MouseAxisScrollVertical = 0;
}