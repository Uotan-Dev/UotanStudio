namespace UotanStudio.Comm.Protocols.Scrcpy;

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
    /// <summary>Server version this client speaks.</summary>
    /// <summary>本客户端对接的服务端版本。</summary>
    public const string ServerVersion = "2.7";

    /// <summary>Device path the server jar is pushed to.</summary>
    /// <summary>服务端 jar 推送到设备上的路径。</summary>
    public const string ServerDevicePath = "/data/local/tmp/scrcpy-server.jar";

    /// <summary>Main class launched through app_process.</summary>
    /// <summary>经 app_process 启动的主类。</summary>
    public const string ServerMainClass = "com.genymobile.scrcpy.Server";

    /// <summary>Legacy no-timestamp sentinel; on the wire scrcpy 2.7 uses PacketFlagConfig.</summary>
    public const ulong NoPts = 0xFFFF_FFFF_FFFF_FFFF;

    /// <summary>scrcpy 2.7 Streamer.PACKET_FLAG_CONFIG.</summary>
    public const ulong PacketFlagConfig = 1UL << 63;

    /// <summary>scrcpy 2.7 Streamer.PACKET_FLAG_KEY_FRAME.</summary>
    public const ulong PacketFlagKeyFrame = 1UL << 62;

    /// <summary>Presentation timestamp bits, expressed in microseconds.</summary>
    public const ulong PacketTimestampMask = PacketFlagKeyFrame - 1;

    /// <summary>Device name field length following the dummy byte on the first socket.</summary>
    /// <summary>首个 socket 上哑字节之后的设备名字段长度。</summary>
    public const int DeviceNameLength = 64;

    /// <summary>Maximum UTF-8 length for INJECT_TEXT.</summary>
    /// <summary>INJECT_TEXT 的 UTF-8 最大长度。</summary>
    public const int MaxTextLength = 300;

    // --- Control message types (scrcpy control_msg.h) -------------------------
    // --- 控制消息类型（scrcpy control_msg.h）-----------------------------------

    /// <summary>Inject a keycode event.</summary>
    /// <summary>注入按键事件。</summary>
    public const byte ControlTypeInjectKeyCode = 0;
    /// <summary>Inject a text event.</summary>
    /// <summary>注入文本事件。</summary>
    public const byte ControlTypeInjectText = 1;
    /// <summary>Inject a touch event.</summary>
    /// <summary>注入触摸事件。</summary>
    public const byte ControlTypeInjectTouchEvent = 2;
    /// <summary>Inject a scroll event.</summary>
    /// <summary>注入滚轮事件。</summary>
    public const byte ControlTypeInjectScrollEvent = 3;
    /// <summary>Back button or wake the screen.</summary>
    /// <summary>返回键或点亮屏幕。</summary>
    public const byte ControlTypeBackOrScreenOn = 4;
    /// <summary>Expand the notification panel.</summary>
    /// <summary>展开通知面板。</summary>
    public const byte ControlTypeExpandNotificationPanel = 5;
    /// <summary>Expand the settings panel.</summary>
    /// <summary>展开设置面板。</summary>
    public const byte ControlTypeExpandSettingsPanel = 6;
    /// <summary>Collapse all panels.</summary>
    /// <summary>收起所有面板。</summary>
    public const byte ControlTypeCollapsePanels = 7;
    /// <summary>Request the device clipboard.</summary>
    /// <summary>请求设备剪贴板。</summary>
    public const byte ControlTypeGetClipboard = 8;
    /// <summary>Set the device clipboard.</summary>
    /// <summary>设置设备剪贴板。</summary>
    public const byte ControlTypeSetClipboard = 9;
    /// <summary>Turn the display power on or off.</summary>
    /// <summary>打开或关闭屏幕电源。</summary>
    public const byte ControlTypeSetDisplayPower = 10;

    // --- Device message types (scrcpy device_msg.h) ----------------------------
    // --- 设备消息类型（scrcpy device_msg.h）-------------------------------------

    /// <summary>Device clipboard content.</summary>
    /// <summary>设备剪贴板内容。</summary>
    public const byte DeviceTypeClipboard = 0;
    /// <summary>Acknowledgment of a sequenced clipboard set.</summary>
    /// <summary>带序号剪贴板写入的确认。</summary>
    public const byte DeviceTypeAckClipboard = 1;

    // --- Event actions ----------------------------------------------------------

    /// <summary>MOTION_EVENT ACTION_DOWN.</summary>
    /// <summary>MOTION_EVENT ACTION_DOWN。</summary>
    public const byte ActionDown = 0;
    /// <summary>MOTION_EVENT ACTION_UP.</summary>
    /// <summary>MOTION_EVENT ACTION_UP。</summary>
    public const byte ActionUp = 1;
    /// <summary>MOTION_EVENT ACTION_MOVE.</summary>
    /// <summary>MOTION_EVENT ACTION_MOVE。</summary>
    public const byte ActionMove = 2;

    /// <summary>Generic-finger pointer id (two's complement i64).</summary>
    /// <summary>通用手指指针 ID（i64 二进制补码）。</summary>
    public const long PointerIdGenericFinger = -1;

    /// <summary>Mouse pointer id (two's complement i64).</summary>
    /// <summary>鼠标指针 ID（i64 二进制补码）。</summary>
    public const long PointerIdMouse = -2;

    /// <summary>AMOTION_EVENT_BUTTON_PRIMARY (left button).</summary>
    /// <summary>AMOTION_EVENT_BUTTON_PRIMARY（左键）。</summary>
    public const uint ButtonLeft = 1 << 0;
    /// <summary>AMOTION_EVENT_BUTTON_SECONDARY (right button).</summary>
    /// <summary>AMOTION_EVENT_BUTTON_SECONDARY（右键）。</summary>
    public const uint ButtonRight = 1 << 1;
    /// <summary>AMOTION_EVENT_BUTTON_TERTIARY (middle button).</summary>
    /// <summary>AMOTION_EVENT_BUTTON_TERTIARY（中键）。</summary>
    public const uint ButtonMiddle = 1 << 2;

    /// <summary>GET_CLIPBOARD copy key: copy.</summary>
    /// <summary>GET_CLIPBOARD 复制键：复制。</summary>
    public const byte CopyKeyCopy = 0;

    /// <summary>GET_CLIPBOARD copy key: cut.</summary>
    /// <summary>GET_CLIPBOARD 复制键：剪切。</summary>
    public const byte CopyKeyCut = 1;
}
