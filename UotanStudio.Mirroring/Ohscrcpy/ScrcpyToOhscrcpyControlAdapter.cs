using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Mirroring;

/// <summary>
/// Translates the scrcpy control messages produced by <see cref="ScrcpyControlMessageWriter"/> into
/// <see cref="OhscrcpyControlAction"/> values for the DevEco-style OpenHarmony mirror path, so the existing
/// mirror UI keeps its single input encoding for both device families.
/// <para>把 <see cref="ScrcpyControlMessageWriter"/> 产生的 scrcpy 控制消息翻译为 <see cref="OhscrcpyControlAction"/>
/// 值，供 DevEco 风格的 OpenHarmony 投屏路径使用，使现有投屏界面在两类设备上继续使用同一套输入编码。</para>
/// </summary>
/// <remarks>
/// <para>
/// Only the message families the mirror view emits are translated: touch, scroll, key code, text,
/// back / screen-on, display power and the decoder reset. Clipboard and UHID messages have no OpenHarmony
/// equivalent and map to <see cref="OhscrcpyControlActionKind.None"/>, which is why the mirror view keeps
/// clipboard features disabled for HDC targets.
/// </para>
/// <para>
/// 只翻译投屏界面实际发出的消息族：触摸、滚轮、键码、文本、返回/点亮、屏幕电源与解码器重置。剪贴板与 UHID
/// 消息在 OpenHarmony 中没有对应物，映射为 <see cref="OhscrcpyControlActionKind.None"/>，因此投屏界面对 HDC
/// 目标应保持剪贴板功能关闭。
/// </para>
/// <para>
/// OpenHarmony key codes differ from Android key codes, so generic key codes are remapped through
/// <see cref="TryMapKeyCode"/>. Back, home, power and volume are the dedicated OpenHarmony codes; the device
/// injects its own down/up pair for them, so only the scrcpy down edge becomes one
/// <see cref="OhscrcpyControlActionKind.KeyPress"/> and the up edge produces nothing. The Android task switcher
/// (<c>KEYCODE_APP_SWITCH</c>) is the OpenHarmony recent-tasks chord <c>META_LEFT</c> + <c>TAB</c>, which is one
/// <see cref="OhscrcpyControlActionKind.KeyChord"/> on its down edge only.
/// </para>
/// <para>
/// OpenHarmony 键码与 Android 键码不同，通用键码经 <see cref="TryMapKeyCode"/> 重映射。返回、主页、电源与音量
/// 使用 OpenHarmony 专用键码；设备侧会为它们自行注入按下与抬起，因此只有 scrcpy 的按下沿会变成一次
/// <see cref="OhscrcpyControlActionKind.KeyPress"/>，抬起沿不产生动作。Android 多任务键
/// （<c>KEYCODE_APP_SWITCH</c>）对应 OpenHarmony 的 <c>META_LEFT</c> + <c>TAB</c> 最近任务组合键，只在按下沿
/// 产生一次 <see cref="OhscrcpyControlActionKind.KeyChord"/>。
/// </para>
/// </remarks>
public static class ScrcpyToOhscrcpyControlAdapter
{
    /// <summary>Byte length of an <c>INJECT_TOUCH_EVENT</c> message.
    /// <para><c>INJECT_TOUCH_EVENT</c> 消息的字节长度。</para></summary>
    private const int TouchMessageLength = 32;

    /// <summary>Byte length of an <c>INJECT_SCROLL_EVENT</c> message.
    /// <para><c>INJECT_SCROLL_EVENT</c> 消息的字节长度。</para></summary>
    private const int ScrollMessageLength = 21;

    /// <summary>Byte length of an <c>INJECT_KEYCODE</c> message.
    /// <para><c>INJECT_KEYCODE</c> 消息的字节长度。</para></summary>
    private const int KeyCodeMessageLength = 14;

    /// <summary>Byte length shared by the length prefix of an <c>INJECT_TEXT</c> message: type byte plus a 32-bit length.
    /// <para><c>INJECT_TEXT</c> 消息长度前缀共享的字节数：类型字节加 32 位长度。</para></summary>
    private const int TextHeaderLength = 5;

    /// <summary>Android <c>KEYCODE_HOME</c>. <para>Android <c>KEYCODE_HOME</c>。</para></summary>
    private const int AndroidKeycodeHome = 3;

    /// <summary>Android <c>KEYCODE_BACK</c>. <para>Android <c>KEYCODE_BACK</c>。</para></summary>
    private const int AndroidKeycodeBack = 4;

    /// <summary>Android <c>KEYCODE_VOLUME_UP</c>. <para>Android <c>KEYCODE_VOLUME_UP</c>。</para></summary>
    private const int AndroidKeycodeVolumeUp = 24;

    /// <summary>Android <c>KEYCODE_VOLUME_DOWN</c>. <para>Android <c>KEYCODE_VOLUME_DOWN</c>。</para></summary>
    private const int AndroidKeycodeVolumeDown = 25;

    /// <summary>Android <c>KEYCODE_POWER</c>. <para>Android <c>KEYCODE_POWER</c>。</para></summary>
    private const int AndroidKeycodePower = 26;

    /// <summary>Android <c>KEYCODE_APP_SWITCH</c>, the recents key the mirror view injects; it becomes the
    /// OpenHarmony <c>META_LEFT</c> + <c>TAB</c> chord.
    /// <para>投屏界面注入的多任务键 Android <c>KEYCODE_APP_SWITCH</c>；它对应 OpenHarmony 的 <c>META_LEFT</c> +
    /// <c>TAB</c> 组合键。</para></summary>
    private const int AndroidKeycodeAppSwitch = 187;

    private const string TouchDownApi = "touchDown";
    private const string TouchMoveApi = "touchMove";
    private const string TouchUpApi = "touchUp";

    /// <summary>Translates one scrcpy control message. <para>翻译一条 scrcpy 控制消息。</para>
    /// </summary>
    /// <param name="message">A complete message produced by <see cref="ScrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="ScrcpyControlMessageWriter"/> 产生的完整消息。</para></param>
    /// <param name="action">The translated action when the method returns <see langword="true"/>; otherwise the
    /// default action whose kind is <see cref="OhscrcpyControlActionKind.None"/>.
    /// <para>方法返回 <see langword="true"/> 时为翻译后的动作；否则为类型是
    /// <see cref="OhscrcpyControlActionKind.None"/> 的默认动作。</para></param>
    /// <returns><see langword="true"/> when the message maps to an action that must be sent; <see langword="false"/>
    /// when the message is empty, truncated, unknown, or deliberately has no OpenHarmony equivalent (clipboard,
    /// UHID, release of a dedicated key).
    /// <para>消息映射为需要发送的动作时为 <see langword="true"/>；消息为空、被截断、未知，或确实没有 OpenHarmony
    /// 对应物（剪贴板、UHID、专用键的抬起）时为 <see langword="false"/>。</para></returns>
    public static bool TryTranslate(ReadOnlySpan<byte> message, out OhscrcpyControlAction action)
    {
        action = default;
        if (message.IsEmpty)
        {
            return false;
        }

        return message[0] switch
        {
            ScrcpyConstants.ControlTypeInjectTouchEvent => TryTranslateTouch(message, out action),
            ScrcpyConstants.ControlTypeInjectScrollEvent => TryTranslateScroll(message, out action),
            ScrcpyConstants.ControlTypeInjectKeyCode => TryTranslateKeyCode(message, out action),
            ScrcpyConstants.ControlTypeInjectText => TryTranslateText(message, out action),
            ScrcpyConstants.ControlTypeBackOrScreenOn => TryTranslateBack(message, out action),
            ScrcpyConstants.ControlTypeSetDisplayPower => TryTranslateDisplayPower(message, out action),
            ScrcpyConstants.ControlTypeResetVideo => TryTranslateResetVideo(out action),
            _ => false,
        };
    }

    /// <summary>Translates one touch message into a touch gesture call.
    /// <para>把一条触摸消息翻译为一次触摸手势调用。</para>
    /// </summary>
    private static bool TryTranslateTouch(ReadOnlySpan<byte> message, out OhscrcpyControlAction action)
    {
        action = default;
        if (message.Length < TouchMessageLength)
        {
            return false;
        }

        var api = message[1] switch
        {
            ScrcpyConstants.ActionDown => TouchDownApi,
            ScrcpyConstants.ActionMove => TouchMoveApi,
            ScrcpyConstants.ActionUp => TouchUpApi,
            3 => TouchUpApi,
            _ => "",
        };
        if (api.Length == 0)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16BigEndian(message[18..]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(message[20..]);
        var x = Clamp(BinaryPrimitives.ReadUInt32BigEndian(message[10..]), width);
        var y = Clamp(BinaryPrimitives.ReadUInt32BigEndian(message[14..]), height);
        action = new OhscrcpyControlAction(OhscrcpyControlActionKind.Gesture, x, y, Gesture: api);
        return true;
    }

    /// <summary>Translates one scroll message into a wheel axis step.
    /// <para>把一条滚轮消息翻译为一次滚轮轴步进。</para>
    /// </summary>
    private static bool TryTranslateScroll(ReadOnlySpan<byte> message, out OhscrcpyControlAction action)
    {
        action = default;
        if (message.Length < ScrollMessageLength)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16BigEndian(message[9..]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(message[11..]);
        var x = Clamp(BinaryPrimitives.ReadUInt32BigEndian(message[1..]), width);
        var y = Clamp(BinaryPrimitives.ReadUInt32BigEndian(message[5..]), height);
        var vertical = BinaryPrimitives.ReadInt16BigEndian(message[15..]);
        action = vertical switch
        {
            > 0 => new OhscrcpyControlAction(OhscrcpyControlActionKind.ScrollUp, x, y),
            < 0 => new OhscrcpyControlAction(OhscrcpyControlActionKind.ScrollDown, x, y),
            _ => default,
        };
        return vertical != 0;
    }

    /// <summary>Translates one key-code message, collapsing dedicated keys into a single press and the Android task
    /// switcher into the OpenHarmony task-switcher chord.
    /// <para>翻译一条键码消息，把专用键折叠为一次按压，并把 Android 多任务键翻译为 OpenHarmony 任务切换组合键。</para>
    /// </summary>
    private static bool TryTranslateKeyCode(ReadOnlySpan<byte> message, out OhscrcpyControlAction action)
    {
        action = default;
        if (message.Length < KeyCodeMessageLength)
        {
            return false;
        }

        var actionValue = message[1];
        if (actionValue is not (ScrcpyConstants.ActionDown or ScrcpyConstants.ActionUp))
        {
            return false;
        }

        var down = actionValue == ScrcpyConstants.ActionDown;
        var keyCode = BinaryPrimitives.ReadInt32BigEndian(message[2..]);
        if (keyCode == AndroidKeycodeAppSwitch)
        {
            if (!down)
            {
                return false;
            }

            action = new OhscrcpyControlAction(
                OhscrcpyControlActionKind.KeyChord,
                KeyCode: OhscrcpyConstants.KeyCodeMetaLeft,
                SecondaryKeyCode: OhscrcpyConstants.KeyCodeTab);
            return true;
        }

        var pressCode = keyCode switch
        {
            AndroidKeycodeBack => OhscrcpyConstants.KeyCodeBack,
            AndroidKeycodeHome => OhscrcpyConstants.KeyCodeHome,
            AndroidKeycodePower => OhscrcpyConstants.KeyCodePower,
            AndroidKeycodeVolumeUp => OhscrcpyConstants.KeyCodeVolumeUp,
            AndroidKeycodeVolumeDown => OhscrcpyConstants.KeyCodeVolumeDown,
            _ => 0,
        };
        if (pressCode != 0)
        {
            if (!down)
            {
                return false;
            }

            action = new OhscrcpyControlAction(OhscrcpyControlActionKind.KeyPress, KeyCode: pressCode);
            return true;
        }

        if (!TryMapKeyCode(keyCode, out var ohKeyCode))
        {
            return false;
        }

        action = new OhscrcpyControlAction(
            down ? OhscrcpyControlActionKind.KeyDown : OhscrcpyControlActionKind.KeyUp, KeyCode: ohKeyCode);
        return true;
    }

    /// <summary>Translates one text message into a UTF-8 text injection.
    /// <para>把一条文本消息翻译为一次 UTF-8 文本注入。</para>
    /// </summary>
    private static bool TryTranslateText(ReadOnlySpan<byte> message, out OhscrcpyControlAction action)
    {
        action = default;
        if (message.Length < TextHeaderLength)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(message[1..]);
        if (length is < 1 or > OhscrcpyConstants.MaximumTextLength || message.Length < TextHeaderLength + length)
        {
            return false;
        }

        action = new OhscrcpyControlAction(
            OhscrcpyControlActionKind.Text, Text: Encoding.UTF8.GetString(message.Slice(TextHeaderLength, length)));
        return true;
    }

    /// <summary>Translates the back-or-screen-on button, which only acts on its down edge.
    /// <para>翻译返回/点亮屏幕按钮，只有按下沿会产生动作。</para>
    /// </summary>
    private static bool TryTranslateBack(ReadOnlySpan<byte> message, out OhscrcpyControlAction action)
    {
        action = default;
        if (message.Length < 2 || message[1] != ScrcpyConstants.ActionDown)
        {
            return false;
        }

        action = new OhscrcpyControlAction(OhscrcpyControlActionKind.KeyPress, KeyCode: OhscrcpyConstants.KeyCodeBack);
        return true;
    }

    /// <summary>Translates the display-power message into one power-key press.
    /// <para>把屏幕电源消息翻译为一次电源键按压。</para>
    /// </summary>
    private static bool TryTranslateDisplayPower(ReadOnlySpan<byte> message, out OhscrcpyControlAction action)
    {
        action = default;
        if (message.Length < 2)
        {
            return false;
        }

        action = new OhscrcpyControlAction(OhscrcpyControlActionKind.KeyPress, KeyCode: OhscrcpyConstants.KeyCodePower);
        return true;
    }

    /// <summary>Translates the decoder reset into a key-frame request.
    /// <para>把解码器重置翻译为一次关键帧请求。</para>
    /// </summary>
    private static bool TryTranslateResetVideo(out OhscrcpyControlAction action)
    {
        action = new OhscrcpyControlAction(OhscrcpyControlActionKind.RequestKeyFrame);
        return true;
    }

    private static int Clamp(uint value, ushort dimension) =>
        dimension == 0 ? (int)Math.Min(value, int.MaxValue) : (int)Math.Min(value, (uint)(dimension - 1));

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