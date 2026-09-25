using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Services.Mirroring;

/// <summary>
/// Encodes scrcpy control messages (client → device direction).
/// <para>scrcpy 控制消息编码器（客户端 → 设备方向）。</para>
/// </summary>
/// <remarks>
/// <para>
/// Message layout follows scrcpy 2.x <c>control_msg.h</c>: a single type byte
/// followed by fixed-width big-endian fields. Touch/scroll positions are
/// expressed in <em>video pixel coordinates</em> of the decoded frame together
/// with the frame size (the server maps them onto the physical display); the
/// server rejects events whose frame size no longer matches its current video
/// size (e.g. after a rotation).
/// </para>
/// <para>
/// 消息布局遵循 scrcpy 2.x <c>control_msg.h</c>：单字节类型 + 定宽大端字段。
/// 触摸/滚轮位置使用解码帧的<em>视频像素坐标</em>并携带帧尺寸（服务端将其映射到
/// 物理屏幕）；帧尺寸与服务端当前视频尺寸不一致（如旋转后）时服务端会拒绝该事件。
/// </para>
/// </remarks>
public static class ScrcpyControlMessageWriter
{
    /// <summary>Encodes a position struct: x(4) y(4) width(2) height(2), big-endian.</summary>
    /// <summary>编码 position 结构：x(4) y(4) width(2) height(2)，大端。</summary>
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

    /// <summary>Builds TYPE_INJECT_KEYCODE (type + action + keycode + repeat + metaState).</summary>
    /// <summary>构建 TYPE_INJECT_KEYCODE（类型 + action + 键码 + 重复次数 + metaState）。</summary>
    /// <param name="action"><see cref="ScrcpyConstants.ActionDown"/> / <see cref="ScrcpyConstants.ActionUp"/> 等。动作类型。</param>
    /// <param name="keyCode">Android keycode (AKEYCODE_*). Android 键码（AKEYCODE_*）。</param>
    /// <param name="repeat">Key repeat count. 键重复次数。</param>
    /// <param name="metaState">AMETA_* modifier mask. AMETA_* 修饰键掩码。</param>
    /// <returns>The encoded message. 编码后的消息。</returns>
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

    /// <summary>Builds TYPE_INJECT_TEXT (type + 4-byte length + UTF-8 bytes).</summary>
    /// <summary>构建 TYPE_INJECT_TEXT（类型 + 4 字节长度 + UTF-8 字节）。</summary>
    /// <param name="text">The text to inject (max 300 UTF-8 bytes). 要注入的文本（UTF-8 最多 300 字节）。</param>
    /// <returns>The encoded message. 编码后的消息。</returns>
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

    /// <summary>Builds TYPE_INJECT_TOUCH_EVENT (32 bytes).</summary>
    /// <summary>构建 TYPE_INJECT_TOUCH_EVENT（32 字节）。</summary>
    /// <param name="action">Motion action (down/up/move). 动作（按下/抬起/移动）。</param>
    /// <param name="pointerId">Pointer id as signed 64-bit (<see cref="ScrcpyConstants.PointerIdMouse"/> = -2). 指针 ID（有符号 64 位，鼠标为 -2）。</param>
    /// <param name="x">X in video pixel coordinates. 视频像素坐标 X。</param>
    /// <param name="y">Y in video pixel coordinates. 视频像素坐标 Y。</param>
    /// <param name="width">Decoded frame width. 解码帧宽度。</param>
    /// <param name="height">Decoded frame height. 解码帧高度。</param>
    /// <param name="pressure">Fixed-point pressure 0..0xFFFF (0xFFFF = 1.0). 定点压力 0..0xFFFF（0xFFFF = 1.0）。</param>
    /// <param name="actionButton">Button associated with the action (press/release). 与动作关联的按键（按下/释放）。</param>
    /// <param name="buttons">Current AMOTION_EVENT_BUTTON_* button state. 当前 AMOTION_EVENT_BUTTON_* 按键状态。</param>
    /// <returns>The encoded message. 编码后的消息。</returns>
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
        // Layout per scrcpy 2.7 ControlMessageReader.parseInjectTouchEvent:
        // type(1)@0 + action(1)@1 + pointerId(8)@2 + position(12)@10 +
        // pressure(2)@22 + actionButton(4)@24 + buttons(4)@28 = 32.
        // 按 scrcpy 2.7 ControlMessageReader.parseInjectTouchEvent 的布局：
        // type(1)@0 + action(1)@1 + pointerId(8)@2 + position(12)@10 +
        // pressure(2)@22 + actionButton(4)@24 + buttons(4)@28 = 32。
        var msg = new byte[32];
        msg[0] = ScrcpyConstants.ControlTypeInjectTouchEvent;
        msg[1] = action;
        // pointerId is written as the raw two's-complement u64.
        // pointerId 按二进制补码 u64 原样写入。
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(2), unchecked((ulong)pointerId));
        WritePosition(msg.AsSpan(10), x, y, width, height);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), pressure);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(24), actionButton);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(28), buttons);
        return msg;
    }

    /// <summary>Builds TYPE_INJECT_SCROLL_EVENT (21 bytes).</summary>
    /// <summary>构建 TYPE_INJECT_SCROLL_EVENT（21 字节）。</summary>
    /// <param name="x">X in video pixel coordinates. 视频像素坐标 X。</param>
    /// <param name="y">Y in video pixel coordinates. 视频像素坐标 Y。</param>
    /// <param name="width">Decoded frame width. 解码帧宽度。</param>
    /// <param name="height">Decoded frame height. 解码帧高度。</param>
    /// <param name="hScroll">Horizontal scroll in ticks, positive scrolls right. 水平滚动刻度，正数向右。</param>
    /// <param name="vScroll">Vertical scroll in ticks, positive scrolls up (wire semantics). 垂直滚动刻度，正数向上（线路语义）。</param>
    /// <param name="buttons">AMOTION_EVENT_BUTTON_* mask. AMOTION_EVENT_BUTTON_* 掩码。</param>
    /// <returns>The encoded message. 编码后的消息。</returns>
    /// <remarks>
    /// <para>
    /// 滚动量为 i16 定点数（1 刻度 = 0x7FFF，对应服务端 1.0f），布局为
    /// type(1) + position(12) + hScroll(2) + vScroll(2) + buttons(4)。
    /// </para>
    /// <para>
    /// Scroll amounts are i16 fixed-point (one tick = 0x7FFF, i.e. 1.0f on the
    /// server); layout: type(1) + position(12) + hScroll(2) + vScroll(2) +
    /// buttons(4).
    /// </para>
    /// </remarks>
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

    /// <summary>Converts ticks to i16 fixed-point (1 tick = 0x7FFF per scrcpy Binary.i16FixedPointToFloat).</summary>
    /// <summary>将刻度转换为 i16 定点数（按 scrcpy Binary.i16FixedPointToFloat，1 刻度 = 0x7FFF）。</summary>
    private static short ToI16FixedPoint(float ticks)
    {
        var scaled = (int)MathF.Round(ticks * 32767f);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    /// <summary>Builds TYPE_BACK_OR_SCREEN_ON (2 bytes).</summary>
    /// <summary>构建 TYPE_BACK_OR_SCREEN_ON（2 字节）。</summary>
    /// <param name="action">Motion action (down/up). 动作（按下/抬起）。</param>
    /// <returns>The encoded message. 编码后的消息。</returns>
    public static byte[] BackOrScreenOn(byte action)
    {
        var msg = new byte[2];
        msg[0] = ScrcpyConstants.ControlTypeBackOrScreenOn;
        msg[1] = action;
        return msg;
    }

    /// <summary>Builds a single-byte control message (panels, display power).</summary>
    /// <summary>构建单字节控制消息（面板、屏幕电源）。</summary>
    /// <param name="type">One of the single-byte control types. 单字节负载的控制类型。</param>
    /// <returns>The encoded message. 编码后的消息。</returns>
    public static byte[] Simple(byte type)
        => type is ScrcpyConstants.ControlTypeExpandNotificationPanel
            or ScrcpyConstants.ControlTypeExpandSettingsPanel
            or ScrcpyConstants.ControlTypeCollapsePanels
            or ScrcpyConstants.ControlTypeSetDisplayPower
            ? [type]
            : throw new ArgumentException("Type is not a single-byte control message. 该类型不是单字节控制消息。", nameof(type));

    /// <summary>Builds TYPE_SET_CLIPBOARD (type + sequence id + paste flag + length + text).</summary>
    /// <summary>构建 TYPE_SET_CLIPBOARD（类型 + 序号 + 粘贴标志 + 长度 + 文本）。</summary>
    /// <param name="text">Clipboard content (max 300 UTF-8 bytes). 剪贴板内容（UTF-8 最多 300 字节）。</param>
    /// <param name="paste">Whether to also paste on the focused field. 是否同时粘贴到焦点输入框。</param>
    /// <param name="sequence">Request sequence echoed by ACK_CLIPBOARD. 由 ACK_CLIPBOARD 回显的请求序号。</param>
    /// <returns>The encoded message. 编码后的消息。</returns>
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

    /// <summary>Builds TYPE_GET_CLIPBOARD (type + copy key).</summary>
    /// <summary>构建 TYPE_GET_CLIPBOARD（类型 + 复制键）。</summary>
    /// <param name="copyKey">Copy or cut. 复制或剪切。</param>
    /// <returns>The encoded message. 编码后的消息。</returns>
    public static byte[] GetClipboard(byte copyKey = ScrcpyConstants.CopyKeyCopy)
    {
        var msg = new byte[2];
        msg[0] = ScrcpyConstants.ControlTypeGetClipboard;
        msg[1] = copyKey;
        return msg;
    }
}