using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Mirroring;

/// <summary>
/// Translates scrcpy control messages produced by <see cref="ScrcpyControlMessageWriter"/> into ohscrcpy
/// control packets, so the existing mirror UI keeps its input encoding for both device families.
/// <para>把 <see cref="ScrcpyControlMessageWriter"/> 产生的 scrcpy 控制消息翻译为 ohscrcpy 控制包，使现有
/// 投屏界面在两类设备上继续使用同一套输入编码。</para>
/// </summary>
/// <remarks>
/// <para>
/// Only the message families the mirror view emits are translated: touch, scroll, key code, text, back /
/// screen-on, display power and the decoder reset. Clipboard and UHID messages have no ohscrcpy equivalent
/// and are dropped, which is why the mirror view keeps clipboard features disabled for HDC targets.
/// </para>
/// <para>
/// 只翻译投屏界面实际发出的消息族：触摸、滚轮、键码、文本、返回/点亮、屏幕电源与解码器重置。剪贴板与
/// UHID 消息在 ohscrcpy 中没有对应物，会被丢弃，因此投屏界面对 HDC 目标应保持剪贴板功能关闭。
/// </para>
/// <para>
/// OpenHarmony key codes differ from Android key codes, so key-code messages are remapped; back, home,
/// power and volume are sent as the dedicated ohscrcpy sub-types. A scrcpy down/up pair for one of those
/// dedicated keys is collapsed into a single press because the device injects its own down/up pair.
/// </para>
/// <para>
/// OpenHarmony 键码与 Android 键码不同，因此键码消息会被重映射；返回、主页、电源与音量使用 ohscrcpy 的
/// 专用子类型。这些专用键的 scrcpy 按下/抬起对会折叠成一次按压，因为设备侧会自行注入按下与抬起。
/// </para>
/// </remarks>
public static class ScrcpyToOhscrcpyControlAdapter
{
    /// <summary>Byte length of an <c>INJECT_TOUCH_EVENT</c> message. <para><c>INJECT_TOUCH_EVENT</c> 消息的字节长度。</para></summary>
    private const int TouchMessageLength = 32;

    /// <summary>Byte length of an <c>INJECT_SCROLL_EVENT</c> message. <para><c>INJECT_SCROLL_EVENT</c> 消息的字节长度。</para></summary>
    private const int ScrollMessageLength = 21;

    /// <summary>Converts one scrcpy fixed-point scroll tick (0x0800) into the float axis the device reads.
    /// <para>把一个 scrcpy 定点滚动刻度（0x0800）换算为设备读取的浮点轴值。</para></summary>
    private const float ScrollTickScale = 1f / (32767f / 16f);

    /// <summary>Translates one scrcpy control message.
    /// <para>翻译一条 scrcpy 控制消息。</para></summary>
    /// <param name="message">A complete message produced by <see cref="ScrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="ScrcpyControlMessageWriter"/> 产生的完整消息。</para></param>
    /// <param name="packet">The framed ohscrcpy control packet, or an empty array when unmapped.
    /// <para>分帧的 ohscrcpy 控制包；无对应物时为空数组。</para></param>
    /// <returns><see langword="true"/> when a packet was produced.
    /// <para>生成了数据包时为 <see langword="true"/>。</para></returns>
    public static bool TryTranslate(ReadOnlySpan<byte> message, out byte[] packet)
    {
        packet = [];
        if (message.IsEmpty) return false;
        switch (message[0])
        {
            case ScrcpyConstants.ControlTypeInjectKeyCode:
                return TryTranslateKeyCode(message, out packet);
            case ScrcpyConstants.ControlTypeInjectText:
                return TryTranslateText(message, out packet);
            case ScrcpyConstants.ControlTypeInjectTouchEvent:
                return TryTranslateTouch(message, out packet);
            case ScrcpyConstants.ControlTypeInjectScrollEvent:
                return TryTranslateScroll(message, out packet);
            case ScrcpyConstants.ControlTypeBackOrScreenOn:
                return TryTranslateBack(message, out packet);
            case ScrcpyConstants.ControlTypeSetDisplayPower:
                return TryTranslateDisplayPower(message, out packet);
            case ScrcpyConstants.ControlTypeResetVideo:
                packet = OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlRestartEncoder);
                return true;
            default:
                return false;
        }
    }

    private static bool TryTranslateKeyCode(ReadOnlySpan<byte> message, out byte[] packet)
    {
        packet = [];
        if (message.Length < 14) return false;
        var action = message[1];
        if (action is not (ScrcpyConstants.ActionDown or ScrcpyConstants.ActionUp)) return false;
        var keyCode = BinaryPrimitives.ReadInt32BigEndian(message[2..]);
        var pressed = action == ScrcpyConstants.ActionDown;
        switch (keyCode)
        {
            case AndroidKeycodeBack:
                if (pressed) packet = OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlBackKey);
                return pressed;
            case AndroidKeycodeHome:
                if (pressed) packet = OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlHomeKey);
                return pressed;
            case AndroidKeycodePower:
                if (pressed) packet = OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlPowerKey);
                return pressed;
            case AndroidKeycodeVolumeUp:
                if (pressed) packet = OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlVolumeUp);
                return pressed;
            case AndroidKeycodeVolumeDown:
                if (pressed) packet = OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlVolumeDown);
                return pressed;
        }

        if (!TryMapKeyCode(keyCode, out var ohKeyCode)) return false;
        packet = OhscrcpyControlMessageWriter.KeyEvent(pressed, ohKeyCode);
        return true;
    }

    private static bool TryTranslateText(ReadOnlySpan<byte> message, out byte[] packet)
    {
        packet = [];
        if (message.Length < 5) return false;
        var length = BinaryPrimitives.ReadInt32BigEndian(message[1..]);
        if (length < 0 || length > OhscrcpyControlMessageWriter.MaxTextLength) return false;
        if (message.Length < 5 + length) return false;
        packet = OhscrcpyControlMessageWriter.Text(Encoding.UTF8.GetString(message.Slice(5, length)));
        return true;
    }

    private static bool TryTranslateTouch(ReadOnlySpan<byte> message, out byte[] packet)
    {
        packet = [];
        if (message.Length < TouchMessageLength) return false;
        var action = message[1];
        var subType = action switch
        {
            ScrcpyConstants.ActionDown => OhscrcpyConstants.ControlTouchDown,
            ScrcpyConstants.ActionMove => OhscrcpyConstants.ControlTouchMove,
            ScrcpyConstants.ActionUp => OhscrcpyConstants.ControlTouchUp,
            3 => OhscrcpyConstants.ControlTouchUp,
            _ => (byte)0,
        };
        if (subType == 0) return false;

        var rawPointer = BinaryPrimitives.ReadInt64BigEndian(message[2..]);
        var pointerId = rawPointer is < 0 or > ushort.MaxValue ? 0 : (int)rawPointer;
        var width = BinaryPrimitives.ReadUInt16BigEndian(message[18..]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(message[20..]);
        var x = Clamp(BinaryPrimitives.ReadUInt32BigEndian(message[10..]), width);
        var y = Clamp(BinaryPrimitives.ReadUInt32BigEndian(message[14..]), height);
        packet = OhscrcpyControlMessageWriter.Touch(subType, x, y, pointerId);
        return true;
    }

    private static bool TryTranslateScroll(ReadOnlySpan<byte> message, out byte[] packet)
    {
        packet = [];
        if (message.Length < ScrollMessageLength) return false;
        var width = BinaryPrimitives.ReadUInt16BigEndian(message[9..]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(message[11..]);
        var x = Clamp(BinaryPrimitives.ReadUInt32BigEndian(message[1..]), width);
        var y = Clamp(BinaryPrimitives.ReadUInt32BigEndian(message[5..]), height);
        var vertical = BinaryPrimitives.ReadInt16BigEndian(message[15..]);
        if (vertical == 0) return false;
        packet = OhscrcpyControlMessageWriter.Scroll(x, y, vertical * ScrollTickScale);
        return true;
    }

    private static bool TryTranslateBack(ReadOnlySpan<byte> message, out byte[] packet)
    {
        packet = [];
        if (message.Length < 2 || message[1] != ScrcpyConstants.ActionDown) return false;
        packet = OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlBackKey);
        return true;
    }

    private static bool TryTranslateDisplayPower(ReadOnlySpan<byte> message, out byte[] packet)
    {
        packet = [];
        if (message.Length < 2) return false;
        packet = OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlPowerKey);
        return true;
    }

    private static int Clamp(uint value, ushort dimension)
        => dimension == 0 ? (int)Math.Min(value, int.MaxValue) : (int)Math.Min(value, (uint)(dimension - 1));

    /// <summary>Android <c>KEYCODE_BACK</c>. <para>Android <c>KEYCODE_BACK</c>。</para></summary>
    private const int AndroidKeycodeBack = 4;
    /// <summary>Android <c>KEYCODE_HOME</c>. <para>Android <c>KEYCODE_HOME</c>。</para></summary>
    private const int AndroidKeycodeHome = 3;
    /// <summary>Android <c>KEYCODE_POWER</c>. <para>Android <c>KEYCODE_POWER</c>。</para></summary>
    private const int AndroidKeycodePower = 26;
    /// <summary>Android <c>KEYCODE_VOLUME_UP</c>. <para>Android <c>KEYCODE_VOLUME_UP</c>。</para></summary>
    private const int AndroidKeycodeVolumeUp = 24;
    /// <summary>Android <c>KEYCODE_VOLUME_DOWN</c>. <para>Android <c>KEYCODE_VOLUME_DOWN</c>。</para></summary>
    private const int AndroidKeycodeVolumeDown = 25;

    /// <summary>Maps an Android key code to the OpenHarmony <c>KeyCode</c> the device injects.
    /// <para>把 Android 键码映射为设备注入的 OpenHarmony <c>KeyCode</c>。</para>
    /// </summary>
    /// <param name="androidKeyCode">Android key code from the scrcpy message.
    /// <para>scrcpy 消息中的 Android 键码。</para></param>
    /// <param name="ohKeyCode">OpenHarmony key code, or 0 when unmapped.
    /// <para>OpenHarmony 键码；无映射时为 0。</para></param>
    /// <returns><see langword="true"/> when the code was mapped.
    /// <para>映射成功时为 <see langword="true"/>。</para></returns>
    public static bool TryMapKeyCode(int androidKeyCode, out int ohKeyCode)
    {
        ohKeyCode = 0;
        if (androidKeyCode is >= 29 and <= 54)
        {
            ohKeyCode = 2017 + (androidKeyCode - 29);
            return true;
        }

        if (androidKeyCode is >= 7 and <= 16)
        {
            ohKeyCode = 2000 + (androidKeyCode - 7);
            return true;
        }

        if (androidKeyCode is >= 131 and <= 142)
        {
            ohKeyCode = 2090 + (androidKeyCode - 131);
            return true;
        }

        ohKeyCode = androidKeyCode switch
        {
            19 => 2012,
            20 => 2013,
            21 => 2014,
            22 => 2015,
            55 => 2043,
            56 => 2044,
            57 => 2045,
            58 => 2046,
            59 => 2047,
            60 => 2048,
            61 => 2049,
            62 => 2050,
            66 => 2054,
            67 => 2055,
            68 => 2056,
            69 => 2057,
            70 => 2058,
            71 => 2059,
            72 => 2060,
            73 => 2061,
            74 => 2062,
            75 => 2063,
            76 => 2064,
            92 => 2068,
            93 => 2069,
            111 => 2070,
            112 => 2071,
            113 => 2072,
            114 => 2073,
            115 => 2074,
            116 => 2075,
            117 => 2076,
            118 => 2077,
            120 => 2079,
            122 => 2081,
            123 => 2082,
            124 => 2083,
            143 => 2102,
            _ => 0,
        };
        return ohKeyCode != 0;
    }
}