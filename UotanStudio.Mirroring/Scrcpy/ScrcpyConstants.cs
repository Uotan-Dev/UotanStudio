namespace UotanStudio.Services.Mirroring;

/// <summary>
/// scrcpy wire-protocol constants for the locked server version.
/// <para>锁定服务端版本的 scrcpy 线协议常量。</para>
/// </summary>
/// <remarks>
/// <para>
/// The client protocol is tightly coupled to the server jar version; these
/// constants target scrcpy <see cref="ServerVersion"/> and must be updated
/// together with the bundled <c>scrcpy-server</c> binary.
/// </para>
/// <para>
/// 客户端协议与服务端 jar 版本强绑定；以下常量面向 scrcpy <see cref="ServerVersion"/>，
/// 与随附的 <c>scrcpy-server</c> 二进制必须同步更新。
/// </para>
/// </remarks>
public static class ScrcpyConstants
{
    /// <summary>Server version string this client protocol targets. <para>本客户端协议对接的服务端版本字符串。</para></summary>
    public const string ServerVersion = "2.7";

    /// <summary>Absolute device-side path the server jar is pushed to. <para>服务端 jar 推送到的设备端绝对路径。</para></summary>
    public const string ServerDevicePath = "/data/local/tmp/scrcpy-server.jar";

    /// <summary>Java main class launched through <c>app_process</c>. <para>经 <c>app_process</c> 启动的 Java 主类。</para></summary>
    public const string ServerMainClass = "com.genymobile.scrcpy.Server";

    /// <summary>Legacy no-timestamp sentinel; on the wire scrcpy 2.7 marks config packets via <see cref="PacketFlagConfig"/> instead. <para>旧式无时间戳哨兵值；线上协议中 scrcpy 2.7 改用 <see cref="PacketFlagConfig"/> 标记配置包。</para></summary>
    public const ulong NoPts = 0xFFFF_FFFF_FFFF_FFFF;

    /// <summary>Stream packet flag identifying a codec configuration packet (bit 63, <c>Streamer.PACKET_FLAG_CONFIG</c>). <para>标识编码器配置包的流数据包标志（第 63 位，<c>Streamer.PACKET_FLAG_CONFIG</c>）。</para></summary>
    public const ulong PacketFlagConfig = 1UL << 63;

    /// <summary>Stream packet flag identifying a key frame (bit 62, <c>Streamer.PACKET_FLAG_KEY_FRAME</c>). <para>标识关键帧的流数据包标志（第 62 位，<c>Streamer.PACKET_FLAG_KEY_FRAME</c>）。</para></summary>
    public const ulong PacketFlagKeyFrame = 1UL << 62;

    /// <summary>Bit mask isolating the microsecond presentation timestamp from the flag bits. <para>用于从标志位中分离出微秒显示时间戳的位掩码。</para></summary>
    public const ulong PacketTimestampMask = PacketFlagKeyFrame - 1;

    /// <summary>Byte length of the fixed UTF-8 device-name field sent after the dummy byte on the first socket. <para>首个 socket 上哑字节之后固定 UTF-8 设备名字段的字节长度。</para></summary>
    public const int DeviceNameLength = 64;

    /// <summary>Maximum UTF-8 byte length accepted for an <c>INJECT_TEXT</c> message. <para>为 <c>INJECT_TEXT</c> 消息允许的最大 UTF-8 字节长度。</para></summary>
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
    /// <summary>Control message type: turn the display power on or off. <para>控制消息类型：打开或关闭屏幕电源。</para></summary>
    public const byte ControlTypeSetDisplayPower = 10;

    /// <summary>Device message type: clipboard content. <para>设备消息类型：剪贴板内容。</para></summary>
    public const byte DeviceTypeClipboard = 0;
    /// <summary>Device message type: acknowledgment of a sequenced clipboard set. <para>设备消息类型：带序号剪贴板写入的确认。</para></summary>
    public const byte DeviceTypeAckClipboard = 1;

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