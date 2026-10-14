namespace UotanStudio.Services.Mirroring;

/// <summary>
/// scrcpy wire-protocol constants for the 5.x server protocol.
/// <para>面向 5.x 服务端协议的 scrcpy 线协议常量。</para>
/// </summary>
/// <remarks>
/// <para>
/// The client protocol is tightly coupled to the deployed server jar; these constants target the
/// 5.x stream layout and must move together with the <c>scrcpy-server</c> binary they are paired
/// with. The version string sent as the first server argument is not a constant here: the server
/// rejects any launch whose first argument is not its own build version, so the value travels with
/// the resolved artifact.
/// </para>
/// <para>
/// 客户端协议与实际部署的服务端 jar 强绑定；以下常量面向 5.x 码流布局，与配对的
/// <c>scrcpy-server</c> 二进制必须同步更新。作为服务端首个参数的版本串不在这里定义：
/// 服务端会拒绝任何首个参数与其自身构建版本不一致的启动，因此该值随已解析的产物一起传递。
/// </para>
/// <para>
/// The server puts the session marker on bit 63, the codec-config marker on bit 62 and the key-frame
/// marker on bit 61, and precedes each stream session with a payload-less session packet. Reading the
/// stream with any other bit table turns that session packet into a flagged frame whose payload length
/// is the video height, so these markers and <see cref="PacketTimestampMask"/> move as one unit with the
/// deployed jar.
/// </para>
/// <para>
/// 服务端把会话标志放在第 63 位、编码配置标志放在第 62 位、关键帧标志放在第 61 位，并在每段会话前发送一个
/// 无负载的会话包。用其他位表读取该码流时，会话包会被当成带标志的帧，其负载长度就是视频高度；因此这些标志位
/// 与 <see cref="PacketTimestampMask"/> 必须随实际部署的 jar 作为一个整体一起变更。
/// </para>
/// </remarks>
public static class ScrcpyConstants
{
    /// <summary>Absolute device-side path the server jar is pushed to. <para>服务端 jar 推送到的设备端绝对路径。</para></summary>
    public const string ServerDevicePath = "/data/local/tmp/scrcpy-server.jar";

    /// <summary>Java main class launched through <c>app_process</c>. <para>经 <c>app_process</c> 启动的 Java 主类。</para></summary>
    public const string ServerMainClass = "com.genymobile.scrcpy.Server";

    /// <summary>Stream packet flag identifying a session metadata packet (bit 63, <c>Streamer.PACKET_FLAG_SESSION</c>).
    /// <para>标识会话元数据包的流数据包标志（第 63 位，<c>Streamer.PACKET_FLAG_SESSION</c>）。</para></summary>
    /// <remarks>
    /// <para>The packet carries no payload: its 12-byte header is the three big-endian integers
    /// flags, width and height, so the flags occupy the high 32 bits of the timestamp word.</para>
    /// <para>该包没有负载：其 12 字节头就是 flags、宽、高三个大端序整数，因此标志位落在时间戳字的高 32 位。</para>
    /// </remarks>
    public const ulong PacketFlagSession = 1UL << 63;

    /// <summary>Stream packet flag identifying a codec configuration packet (bit 62, <c>Streamer.PACKET_FLAG_CONFIG</c>).
    /// <para>标识编码器配置包的流数据包标志（第 62 位，<c>Streamer.PACKET_FLAG_CONFIG</c>）。</para></summary>
    public const ulong PacketFlagConfig = 1UL << 62;

    /// <summary>Stream packet flag identifying a key frame (bit 61, <c>Streamer.PACKET_FLAG_KEY_FRAME</c>).
    /// <para>标识关键帧的流数据包标志（第 61 位，<c>Streamer.PACKET_FLAG_KEY_FRAME</c>）。</para></summary>
    public const ulong PacketFlagKeyFrame = 1UL << 61;

    /// <summary>Bit mask isolating the microsecond presentation timestamp from the flag bits.
    /// <para>用于从标志位中分离出微秒显示时间戳的位掩码。</para></summary>
    public const ulong PacketTimestampMask = PacketFlagKeyFrame - 1;

    /// <summary>Session flag bit reporting that the size change was requested by the client
    /// (<c>isClientResize</c>, bit 0 of the flags integer, i.e. bit 32 of the timestamp word).
    /// <para>表示尺寸变化由客户端请求的会话标志位（<c>isClientResize</c>，flags 整数第 0 位，即时间戳字第 32 位）。</para></summary>
    public const ulong SessionFlagClientResize = 1UL << 32;

    /// <summary>Byte length of a framed packet header, shared by the video and audio sockets
    /// (8-byte timestamp word plus 4-byte payload length, or 3 big-endian integers for a session packet).
    /// <para>分帧包头部的字节长度，视频与音频 socket 相同（8 字节时间戳字加 4 字节负载长度；会话包则为 3 个大端序整数）。</para></summary>
    public const int HeaderLength = 12;

    /// <summary>Byte length of the codec identifier announced once per stream when stream metadata is sent.
    /// <para>发送流元数据时，每条流开头宣告一次的编码标识符字节长度。</para></summary>
    public const int StreamMetaLength = 4;

    /// <summary>Codec identifier meaning the server gave up on this stream and the client must continue
    /// without it (written as a 4-byte identifier of 0 by <c>Streamer.writeDisableStream</c>).
    /// <para>表示服务端已放弃该流、客户端须在不使用它的情况下继续的编码标识符（<c>Streamer.writeDisableStream</c> 写入值为 0 的 4 字节标识符）。</para></summary>
    public const uint StreamDisabledByServer = 0;

    /// <summary>Codec identifier meaning the server hit a configuration error and the whole session must
    /// stop (written as a 4-byte identifier of 1 by <c>Streamer.writeDisableStream</c>).
    /// <para>表示服务端遇到配置错误、整个会话必须停止的编码标识符（<c>Streamer.writeDisableStream</c> 写入值为 1 的 4 字节标识符）。</para></summary>
    public const uint StreamDisabledByError = 1;

    /// <summary>Byte length of the fixed UTF-8 device-name field sent after the dummy byte on the first socket.
    /// <para>首个 socket 上哑字节之后固定 UTF-8 设备名字段的字节长度。</para></summary>
    public const int DeviceNameLength = 64;

    /// <summary>Maximum UTF-8 byte length accepted for an <c>INJECT_TEXT</c> message
    /// (<c>ControlMessageReader.INJECT_TEXT_MAX_LENGTH</c>).
    /// <para>为 <c>INJECT_TEXT</c> 消息允许的最大 UTF-8 字节长度（<c>ControlMessageReader.INJECT_TEXT_MAX_LENGTH</c>）。</para></summary>
    public const int MaxTextLength = 300;

    /// <summary>Control message type: inject a key-code event. <para>控制消息类型：注入按键事件。</para></summary>
    public const byte ControlTypeInjectKeyCode = 0;
    /// <summary>Control message type: inject a text event. <para>控制消息类型：注入文本事件。</para></summary>
    public const byte ControlTypeInjectText = 1;
    /// <summary>Control message type: inject a touch event. <para>控制消息类型：注入触摸事件。</para></summary>
    public const byte ControlTypeInjectTouchEvent = 2;
    /// <summary>Control message type: inject a scroll event. <para>控制消息类型：注入滚轮事件。</para></summary>
    public const byte ControlTypeInjectScrollEvent = 3;
    /// <summary>Control message type: back button, or wake the screen when off. <para>控制消息类型：返回键，或在息屏时点亮屏幕。</para></summary>
    public const byte ControlTypeBackOrScreenOn = 4;
    /// <summary>Control message type: expand the notification panel. <para>控制消息类型：展开通知面板。</para></summary>
    public const byte ControlTypeExpandNotificationPanel = 5;
    /// <summary>Control message type: expand the settings panel. <para>控制消息类型：展开设置面板。</para></summary>
    public const byte ControlTypeExpandSettingsPanel = 6;
    /// <summary>Control message type: collapse all panels. <para>控制消息类型：收起所有面板。</para></summary>
    public const byte ControlTypeCollapsePanels = 7;
    /// <summary>Control message type: request the device clipboard. <para>控制消息类型：请求设备剪贴板。</para></summary>
    public const byte ControlTypeGetClipboard = 8;
    /// <summary>Control message type: set the device clipboard. <para>控制消息类型：设置设备剪贴板。</para></summary>
    public const byte ControlTypeSetClipboard = 9;
    /// <summary>Control message type: set the display power state.
    /// <para>控制消息类型：设置屏幕电源状态。</para></summary>
    /// <remarks><para>类型字节后还有一个布尔字节，取值见 <see cref="DisplayPowerOn"/> 与
    /// <see cref="DisplayPowerOff"/>；只发类型字节会让服务端把下一条控制消息的首字节当作布尔值消费，
    /// 解析随即错位并以协议异常终止服务端进程。</para>
    /// <para>A boolean byte follows the type, one of <see cref="DisplayPowerOn"/> or
    /// <see cref="DisplayPowerOff"/>; omitting it makes the server consume the first byte of the next
    /// control message as the flag, desynchronizing the stream and killing the server process with a
    /// protocol error.</para></remarks>
    public const byte ControlTypeSetDisplayPower = 10;
    /// <summary>Control message type: rotate the device. <para>控制消息类型：旋转设备。</para></summary>
    public const byte ControlTypeRotateDevice = 11;
    /// <summary>Control message type: create a UHID device (5.x only). <para>控制消息类型：创建 UHID 设备（仅 5.x）。</para></summary>
    public const byte ControlTypeUhidCreate = 12;
    /// <summary>Control message type: send a UHID report (5.x only). <para>控制消息类型：发送 UHID 报告（仅 5.x）。</para></summary>
    public const byte ControlTypeUhidInput = 13;
    /// <summary>Control message type: destroy a UHID device (5.x only). <para>控制消息类型：销毁 UHID 设备（仅 5.x）。</para></summary>
    public const byte ControlTypeUhidDestroy = 14;
    /// <summary>Control message type: open the hard-keyboard settings (5.x only). <para>控制消息类型：打开实体键盘设置（仅 5.x）。</para></summary>
    public const byte ControlTypeOpenHardKeyboardSettings = 15;
    /// <summary>Control message type: start an application (5.x only). <para>控制消息类型：启动应用（仅 5.x）。</para></summary>
    public const byte ControlTypeStartApp = 16;
    /// <summary>Control message type: reset the video decoder session (5.x only). <para>控制消息类型：重置视频解码会话（仅 5.x）。</para></summary>
    public const byte ControlTypeResetVideo = 17;
    /// <summary>Control message type: set the camera torch mode (5.x only). <para>控制消息类型：设置相机闪光灯模式（仅 5.x）。</para></summary>
    public const byte ControlTypeCameraSetTorch = 18;
    /// <summary>Control message type: zoom the camera in (5.x only). <para>控制消息类型：相机放大（仅 5.x）。</para></summary>
    public const byte ControlTypeCameraZoomIn = 19;
    /// <summary>Control message type: zoom the camera out (5.x only). <para>控制消息类型：相机缩小（仅 5.x）。</para></summary>
    public const byte ControlTypeCameraZoomOut = 20;
    /// <summary>Control message type: request a display resize (5.x only). <para>控制消息类型：请求调整显示尺寸（仅 5.x）。</para></summary>
    public const byte ControlTypeResizeDisplay = 21;
    /// <summary>Control message type: scan a pushed file (5.x only). <para>控制消息类型：扫描已推送的文件（仅 5.x）。</para></summary>
    public const byte ControlTypeScanFile = 22;

    /// <summary>Display power flag 0: the display is powered off. <para>屏幕电源标志 0：屏幕关闭。</para></summary>
    public const byte DisplayPowerOff = 0;

    /// <summary>Display power flag 1: the display is powered on. <para>屏幕电源标志 1：屏幕点亮。</para></summary>
    public const byte DisplayPowerOn = 1;

    /// <summary>Device message type: clipboard content. <para>设备消息类型：剪贴板内容。</para></summary>
    public const byte DeviceTypeClipboard = 0;
    /// <summary>Device message type: acknowledgment of a sequenced clipboard set. <para>设备消息类型：带序号剪贴板写入的确认。</para></summary>
    public const byte DeviceTypeAckClipboard = 1;
    /// <summary>Device message type: UHID output report (5.x only). <para>设备消息类型：UHID 输出报告（仅 5.x）。</para></summary>
    public const byte DeviceTypeUhidOutput = 2;

    /// <summary>MOTION_EVENT <c>ACTION_DOWN</c> action value. <para>MOTION_EVENT 的 <c>ACTION_DOWN</c> 动作值。</para></summary>
    public const byte ActionDown = 0;
    /// <summary>MOTION_EVENT <c>ACTION_UP</c> action value. <para>MOTION_EVENT 的 <c>ACTION_UP</c> 动作值。</para></summary>
    public const byte ActionUp = 1;
    /// <summary>MOTION_EVENT <c>ACTION_MOVE</c> action value. <para>MOTION_EVENT 的 <c>ACTION_MOVE</c> 动作值。</para></summary>
    public const byte ActionMove = 2;

    /// <summary>Generic-finger pointer id, sent as a two's-complement 64-bit integer. <para>通用手指指针 ID，以 64 位二进制补码整数发送。</para></summary>
    public const long PointerIdGenericFinger = -1;

    /// <summary>Mouse pointer id, sent as a two's-complement 64-bit integer. <para>鼠标指针 ID，以 64 位二进制补码整数发送。</para></summary>
    public const long PointerIdMouse = -2;

    /// <summary>Button mask <c>AMOTION_EVENT_BUTTON_PRIMARY</c> (left button). <para>按键掩码 <c>AMOTION_EVENT_BUTTON_PRIMARY</c>（左键）。</para></summary>
    public const uint ButtonLeft = 1 << 0;
    /// <summary>Button mask <c>AMOTION_EVENT_BUTTON_SECONDARY</c> (right button). <para>按键掩码 <c>AMOTION_EVENT_BUTTON_SECONDARY</c>（右键）。</para></summary>
    public const uint ButtonRight = 1 << 1;
    /// <summary>Button mask <c>AMOTION_EVENT_BUTTON_TERTIARY</c> (middle button). <para>按键掩码 <c>AMOTION_EVENT_BUTTON_TERTIARY</c>（中键）。</para></summary>
    public const uint ButtonMiddle = 1 << 2;

    /// <summary><c>GET_CLIPBOARD</c> copy key requesting a copy. <para>用于 <c>GET_CLIPBOARD</c>、请求复制的复制键。</para></summary>
    public const byte CopyKeyCopy = 0;

    /// <summary><c>GET_CLIPBOARD</c> copy key requesting a cut. <para>用于 <c>GET_CLIPBOARD</c>、请求剪切的复制键。</para></summary>
    public const byte CopyKeyCut = 1;
}