using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Mirroring;

/// <summary>
/// Encodes scrcpy control messages (client → device direction).
/// <para>scrcpy 控制消息编码器（客户端 → 设备方向）。</para>
/// </summary>
/// <remarks>
/// <para>
/// Message layout follows the server's <c>ControlMessageReader</c>: a single type byte
/// followed by fixed-width big-endian fields. Touch/scroll positions are
/// expressed in <em>video pixel coordinates</em> of the decoded frame together
/// with the frame size (the server maps them onto the physical display); the
/// server rejects events whose frame size differs from its current video
/// size (e.g. after a rotation).
/// </para>
/// <para>
/// 消息布局遵循服务端 <c>ControlMessageReader</c>：单字节类型 + 定宽大端字段。
/// 触摸/滚轮位置使用解码帧的<em>视频像素坐标</em>并携带帧尺寸（服务端将其映射到
/// 物理屏幕）；帧尺寸与服务端当前视频尺寸不一致（如旋转后）时服务端会拒绝该事件。
/// </para>
/// </remarks>
public static class ScrcpyControlMessageWriter
{
    /// <summary>Encodes a position struct: x(4) y(4) width(2) height(2), big-endian.
    /// <para>编码 position 结构：x(4) y(4) width(2) height(2)，大端。</para>
    /// </summary>
    private static void WritePosition(Span<byte> dest, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame size must be positive. 帧尺寸必须为正。");
        }

        if ((uint)x > (uint)width || (uint)y > (uint)height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must lie within the frame. 坐标必须位于帧内。");
        }

        BinaryPrimitives.WriteUInt32BigEndian(dest, (uint)x);
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(4), (uint)y);
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(8), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(10), (ushort)height);
    }

    /// <summary>
    /// Builds TYPE_INJECT_KEYCODE (type + action + keycode + repeat + metaState), 14 bytes.
    /// <para>构建 TYPE_INJECT_KEYCODE（类型 + action + 键码 + 重复次数 + metaState），共 14 字节。</para>
    /// </summary>
    /// <param name="action">Action type, e.g. <see cref="ScrcpyConstants.ActionDown"/> or
    /// <see cref="ScrcpyConstants.ActionUp"/>.
    /// <para>动作类型（如 <see cref="ScrcpyConstants.ActionDown"/> / <see cref="ScrcpyConstants.ActionUp"/>）。</para></param>
    /// <param name="keyCode">Android keycode (AKEYCODE_*). <para>Android 键码（AKEYCODE_*）。</para></param>
    /// <param name="repeat">Key repeat count. <para>键重复次数。</para></param>
    /// <param name="metaState">AMETA_* modifier mask. <para>AMETA_* 修饰键掩码。</para></param>
    /// <returns>The encoded message. <para>编码后的消息。</para></returns>
    public static byte[] InjectKeyCode(byte action, int keyCode, int repeat, int metaState)
    {
        var msg = new byte[14];
        msg[0] = ScrcpyConstants.ControlTypeInjectKeyCode;
        msg[1] = action;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(2), keyCode);
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(6), repeat);
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(10), metaState);
        return msg;
    }

    /// <summary>
    /// Builds TYPE_INJECT_TEXT (type + 4-byte length + UTF-8 bytes).
    /// <para>构建 TYPE_INJECT_TEXT（类型 + 4 字节长度 + UTF-8 字节）。</para>
    /// </summary>
    /// <param name="text">The text to inject (max 300 UTF-8 bytes).
    /// <para>要注入的文本（UTF-8 最多 300 字节）。</para></param>
    /// <returns>The encoded message. <para>编码后的消息。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> encodes to more than 300 UTF-8 bytes.
    /// <para><paramref name="text"/> 的 UTF-8 编码超过 300 字节。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    public static byte[] InjectText(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length > ScrcpyConstants.MaxTextLength)
        {
            throw new ArgumentException(
                $"Text exceeds the {ScrcpyConstants.MaxTextLength}-byte limit. 文本超过 {ScrcpyConstants.MaxTextLength} 字节上限。",
                nameof(text));
        }

        var msg = new byte[5 + payload.Length];
        msg[0] = ScrcpyConstants.ControlTypeInjectText;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(1), payload.Length);
        payload.CopyTo(msg, 5);
        return msg;
    }

    /// <summary>Builds TYPE_INJECT_TOUCH_EVENT (32 bytes).
    /// <para>构建 TYPE_INJECT_TOUCH_EVENT（32 字节）。</para></summary>
    /// <param name="action">Motion action (down/up/move).<para>动作（按下/抬起/移动）。</para></param>
    /// <param name="pointerId">Pointer id as signed 64-bit (<see cref="ScrcpyConstants.PointerIdMouse"/> = -2).
    /// <para>指针 ID（有符号 64 位，鼠标为 -2）。</para></param>
    /// <param name="x">X in video pixel coordinates.<para>视频像素坐标 X。</para></param>
    /// <param name="y">Y in video pixel coordinates.<para>视频像素坐标 Y。</para></param>
    /// <param name="width">Decoded frame width.<para>解码帧宽度。</para></param>
    /// <param name="height">Decoded frame height.<para>解码帧高度。</para></param>
    /// <param name="pressure">Fixed-point pressure 0..0xFFFF (0xFFFF = 1.0).
    /// <para>定点压力 0..0xFFFF（0xFFFF = 1.0）。</para></param>
    /// <param name="actionButton">Button associated with the action (press/release).
    /// <para>与动作关联的按键（按下/释放）。</para></param>
    /// <param name="buttons">Current AMOTION_EVENT_BUTTON_* button state.
    /// <para>当前 AMOTION_EVENT_BUTTON_* 按键状态。</para></param>
    /// <returns>The encoded message.<para>编码后的消息。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive, or a coordinate is outside the frame.
    /// <para><paramref name="width"/> 或 <paramref name="height"/> 不为正，或坐标超出帧范围。</para></exception>
    public static byte[] InjectTouchEvent(
        byte action,
        long pointerId,
        int x,
        int y,
        int width,
        int height,
        ushort pressure = 0xFFFF,
        uint actionButton = 0,
        uint buttons = 0)
    {
        var msg = new byte[32];
        msg[0] = ScrcpyConstants.ControlTypeInjectTouchEvent;
        msg[1] = action;
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(2), unchecked((ulong)pointerId));
        WritePosition(msg.AsSpan(10), x, y, width, height);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), pressure);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(24), actionButton);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(28), buttons);
        return msg;
    }

    /// <summary>Builds TYPE_INJECT_SCROLL_EVENT (21 bytes).
    /// <para>构建 TYPE_INJECT_SCROLL_EVENT（21 字节）。</para></summary>
    /// <param name="x">X in video pixel coordinates.<para>视频像素坐标 X。</para></param>
    /// <param name="y">Y in video pixel coordinates.<para>视频像素坐标 Y。</para></param>
    /// <param name="width">Decoded frame width.<para>解码帧宽度。</para></param>
    /// <param name="height">Decoded frame height.<para>解码帧高度。</para></param>
    /// <param name="hScroll">Horizontal scroll in ticks, positive scrolls right.
    /// <para>水平滚动刻度，正数向右。</para></param>
    /// <param name="vScroll">Vertical scroll in ticks, positive scrolls up (wire semantics).
    /// <para>垂直滚动刻度，正数向上（线路语义）。</para></param>
    /// <param name="buttons">AMOTION_EVENT_BUTTON_* mask.
    /// <para>AMOTION_EVENT_BUTTON_* 掩码。</para></param>
    /// <returns>The encoded message.<para>编码后的消息。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive, or a coordinate is outside the frame.
    /// <para><paramref name="width"/> 或 <paramref name="height"/> 不为正，或坐标超出帧范围。</para></exception>
    /// <remarks>
    /// <para> 滚动量为 i16 定点数，布局为 type(1) + position(12) + hScroll(2) + vScroll(2) + buttons(4)。 服务端会把解码出的 [-1, 1] 定点值再乘 16 才作为最终滚动量（其注释说明实际范围是 [-16, 16]）， 因此 1 刻度对应 0x0800。 </para>
    /// <para> Scroll amounts are i16 fixed-point; layout: type(1) + position(12) + hScroll(2) + vScroll(2) + buttons(4). The server multiplies the decoded [-1, 1] fixed-point value by 16 before using it (its reader comment states the real range is [-16, 16]), so one tick is 0x0800. </para></remarks>
    public static byte[] InjectScrollEvent(int x, int y, int width, int height, float hScroll, float vScroll, uint buttons = 0)
    {
        var msg = new byte[21];
        msg[0] = ScrcpyConstants.ControlTypeInjectScrollEvent;
        WritePosition(msg.AsSpan(1), x, y, width, height);
        BinaryPrimitives.WriteInt16BigEndian(msg.AsSpan(13), ToI16FixedPoint(hScroll));
        BinaryPrimitives.WriteInt16BigEndian(msg.AsSpan(15), ToI16FixedPoint(vScroll));
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(17), buttons);
        return msg;
    }

    /// <summary>Converts ticks to the i16 fixed-point scale the 5.x reader expects (1 tick = 0x0800,
    /// because the server multiplies the decoded value by 16).
    /// <para>按 5.x 读取端期望的 i16 定点比例换算刻度（1 刻度 = 0x0800，因为服务端会把解码值再乘 16）。</para>
    /// </summary>
    private static short ToI16FixedPoint(float ticks)
    {
        var scaled = (int)MathF.Round(ticks * (32767f / 16f));
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Builds TYPE_BACK_OR_SCREEN_ON (2 bytes).
    /// <para>构建 TYPE_BACK_OR_SCREEN_ON（2 字节）。</para>
    /// </summary>
    /// <param name="action">Motion action (down/up). <para>动作（按下/抬起）。</para></param>
    /// <returns>The encoded message. <para>编码后的消息。</para></returns>
    public static byte[] BackOrScreenOn(byte action)
    {
        var msg = new byte[2];
        msg[0] = ScrcpyConstants.ControlTypeBackOrScreenOn;
        msg[1] = action;
        return msg;
    }

    /// <summary>
    /// Builds a single-byte control message (panel expansion and collapse).
    /// <para>构建单字节控制消息（展开与收起面板）。</para>
    /// </summary>
    /// <param name="type">One of the single-byte control types. <para>单字节负载的控制类型。</para></param>
    /// <returns>The encoded message. <para>编码后的消息。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not one of the single-byte control types.
    /// <para><paramref name="type"/> 不属于单字节控制消息类型。</para></exception>
    public static byte[] Simple(byte type)
        => type is ScrcpyConstants.ControlTypeExpandNotificationPanel
            or ScrcpyConstants.ControlTypeExpandSettingsPanel
            or ScrcpyConstants.ControlTypeCollapsePanels
            ? [type]
            : throw new ArgumentException("Type is not a single-byte control message. 该类型不是单字节控制消息。", nameof(type));

    /// <summary>
    /// Builds TYPE_RESET_VIDEO: a request for the server to reconfigure the encoder and emit a new
    /// independent frame, which is the only safe entry point for a decoder rebuilt mid-stream.
    /// <para>构建 TYPE_RESET_VIDEO：请求服务端重配置编码器并产出一个新的独立帧，这是流内重建解码器唯一安全的入口。</para>
    /// </summary>
    /// <returns>
    /// The encoded one-byte message. <para>编码后的单字节消息。</para>
    /// </returns>
    public static byte[] ResetVideo() => [ScrcpyConstants.ControlTypeResetVideo];

    /// <summary>
    /// Builds TYPE_SET_DISPLAY_POWER (type + power flag), 2 bytes.
    /// <para>构建 TYPE_SET_DISPLAY_POWER（类型 + 电源标志），共 2 字节。</para>
    /// </summary>
    /// <param name="on">Whether the display is powered on; the flag byte is
    /// <see cref="ScrcpyConstants.DisplayPowerOn"/> or <see cref="ScrcpyConstants.DisplayPowerOff"/>.
    /// <para>屏幕是否点亮；标志字节为 <see cref="ScrcpyConstants.DisplayPowerOn"/> 或
    /// <see cref="ScrcpyConstants.DisplayPowerOff"/>。</para></param>
    /// <returns>The encoded message. <para>编码后的消息。</para></returns>
    /// <remarks>
    /// <para>服务端用 <c>DataInputStream.readBoolean()</c> 读取第二字节并直接传给
    /// <c>Device.setDisplayPower(displayId, on)</c>，所以该载荷是布尔标志，不是 SurfaceControl 的模式值。</para>
    /// <para>The server reads the second byte with <c>DataInputStream.readBoolean()</c> and passes it
    /// straight to <c>Device.setDisplayPower(displayId, on)</c>, so the payload is a boolean flag rather
    /// than a SurfaceControl power mode.</para>
    /// </remarks>
    public static byte[] SetDisplayPower(bool on)
        => [ScrcpyConstants.ControlTypeSetDisplayPower, on ? ScrcpyConstants.DisplayPowerOn : ScrcpyConstants.DisplayPowerOff];

    /// <summary>Builds TYPE_SET_CLIPBOARD (type + sequence id + paste flag + length + text).
    /// <para>构建 TYPE_SET_CLIPBOARD（类型 + 序号 + 粘贴标志 + 长度 + 文本）。</para></summary>
    /// <param name="text">Clipboard content (max 300 UTF-8 bytes).
    /// <para>剪贴板内容（UTF-8 最多 300 字节）。</para></param>
    /// <param name="paste">Whether to also paste on the focused field.
    /// <para>是否同时粘贴到焦点输入框。</para></param>
    /// <param name="sequence">Request sequence echoed by ACK_CLIPBOARD.
    /// <para>由 ACK_CLIPBOARD 回显的请求序号。</para></param>
    /// <returns>The encoded message.<para>编码后的消息。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> encodes to more than 300 UTF-8 bytes.
    /// <para><paramref name="text"/> 的 UTF-8 编码超过 300 字节。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    public static byte[] SetClipboard(string text, bool paste, ulong sequence = 0)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length > ScrcpyConstants.MaxTextLength)
        {
            throw new ArgumentException(
                $"Text exceeds the {ScrcpyConstants.MaxTextLength}-byte limit. 文本超过 {ScrcpyConstants.MaxTextLength} 字节上限。",
                nameof(text));
        }

        var msg = new byte[14 + payload.Length];
        msg[0] = ScrcpyConstants.ControlTypeSetClipboard;
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(1), sequence);
        msg[9] = paste ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(10), payload.Length);
        payload.CopyTo(msg, 14);
        return msg;
    }

    /// <summary>
    /// Builds TYPE_GET_CLIPBOARD (type + copy key).
    /// <para>构建 TYPE_GET_CLIPBOARD（类型 + 复制键）。</para>
    /// </summary>
    /// <param name="copyKey">Copy or cut. <para>复制或剪切。</para></param>
    /// <returns>The encoded message. <para>编码后的消息。</para></returns>
    public static byte[] GetClipboard(byte copyKey = ScrcpyConstants.CopyKeyCopy)
    {
        var msg = new byte[2];
        msg[0] = ScrcpyConstants.ControlTypeGetClipboard;
        msg[1] = copyKey;
        return msg;
    }
}