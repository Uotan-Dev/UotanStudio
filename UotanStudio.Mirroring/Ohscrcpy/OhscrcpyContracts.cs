namespace UotanStudio.Mirroring;

/// <summary>
/// One input step translated from a scrcpy control message into the OpenHarmony <c>uinput</c> command set.
/// <para>由 scrcpy 控制消息翻译为 OpenHarmony <c>uinput</c> 命令集的一个输入步骤。</para>
/// </summary>
internal enum OhscrcpyKeyAction
{
    /// <summary>Press the key down. <para>按下按键。</para></summary>
    Down,
    /// <summary>Release the key. <para>释放按键。</para></summary>
    Up,
    /// <summary>Press and release the key in one command. <para>用一条命令完成按下与释放。</para></summary>
    Press,
}

/// <summary>Kind of one action produced by the scrcpy-to-OpenHarmony control translation.
/// <para>scrcpy 到 OpenHarmony 控制翻译产生的一个动作类型。</para>
/// </summary>
public enum OhscrcpyControlActionKind
{
    /// <summary>No action; the message is unsupported. <para>无动作；该消息不受支持。</para></summary>
    None,
    /// <summary>One gesture API call. <para>一次手势 API 调用。</para></summary>
    Gesture,
    /// <summary>One wheel-up axis step. <para>一次向上滚轮轴步进。</para></summary>
    ScrollUp,
    /// <summary>One wheel-down axis step. <para>一次向下滚轮轴步进。</para></summary>
    ScrollDown,
    /// <summary>Press one key down. <para>按下一个键。</para></summary>
    KeyDown,
    /// <summary>Release one key. <para>释放一个键。</para></summary>
    KeyUp,
    /// <summary>Press and release one key. <para>按下并释放一个键。</para></summary>
    KeyPress,
    /// <summary>Inject UTF-8 text. <para>注入 UTF-8 文本。</para></summary>
    Text,
    /// <summary>Ask the device for a new key frame. <para>请求设备产生新的关键帧。</para></summary>
    RequestKeyFrame,
    /// <summary>Press and release two keys together as one chord. <para>把两个键作为一次组合键同时按下并释放。</para></summary>
    KeyChord,
}

/// <summary>One translated control action with its coordinates, key code or text.
/// <para>一个已翻译的控制动作及其坐标、键码或文本。</para>
/// </summary>
/// <param name="Kind">Action kind. <para>动作类型。</para></param>
/// <param name="X">Display X for gestures. <para>手势的显示坐标 X。</para></param>
/// <param name="Y">Display Y for gestures. <para>手势的显示坐标 Y。</para></param>
/// <param name="KeyCode">OpenHarmony key code for key actions, or the modifier of a chord.
/// <para>按键动作的 OpenHarmony 键码；组合键动作时为修饰键。</para></param>
/// <param name="Text">UTF-8 text for text actions. <para>文本动作的 UTF-8 文本。</para></param>
/// <param name="Gesture">Gesture API name for gesture actions. <para>手势动作的 API 名称。</para></param>
/// <param name="SecondaryKeyCode">Chord partner of <paramref name="KeyCode"/>; zero for other actions.
/// <para><paramref name="KeyCode"/> 的组合键配对键；其它动作为零。</para></param>
public readonly record struct OhscrcpyControlAction(
    OhscrcpyControlActionKind Kind,
    int X = 0,
    int Y = 0,
    int KeyCode = 0,
    string Text = "",
    string Gesture = "",
    int SecondaryKeyCode = 0);

/// <summary>
/// UI Test control channel: gesture RPC over the daemon socket plus shell-based key and text injection.
/// <para>UI Test 控制通道：经守护进程 socket 的手势 RPC，以及基于 shell 的按键与文本注入。</para>
/// </summary>
internal interface IOhscrcpyControlChannel : IDisposable
{
    /// <summary>Whether the JSON control socket is connected. <para>JSON 控制 socket 是否已连接。</para></summary>
    bool IsConnected { get; }

    /// <summary>Logical display width reported by the control handshake, or zero when unknown.
    /// <para>控制握手报告的逻辑显示宽度；未知时为零。</para></summary>
    int DisplayWidth { get; }

    /// <summary>Logical display height reported by the control handshake, or zero when unknown.
    /// <para>控制握手报告的逻辑显示高度；未知时为零。</para></summary>
    int DisplayHeight { get; }

    /// <summary>Opens the forwarded socket and completes the display-size handshake.
    /// <para>打开已转发的 socket 并完成显示尺寸握手。</para></summary>
    void Connect();

    /// <summary>Queues one gesture API call with raw display coordinates.
    /// <para>按原始显示坐标排队一次手势 API 调用。</para></summary>
    void SendGesture(string api, int x, int y);

    /// <summary>Queues one wheel step as an axis gesture followed by a stop.
    /// <para>把一次滚轮步进排队为轴手势加停止。</para></summary>
    void SendScroll(bool up, int x, int y);

    /// <summary>Queues one OpenHarmony key action through <c>uinput</c>.
    /// <para>经 <c>uinput</c> 排队一次 OpenHarmony 按键动作。</para></summary>
    void SendKey(int keyCode, OhscrcpyKeyAction action);

    /// <summary>Queues one two-key chord through <c>uinput</c>: both keys go down and come back up in one command.
    /// <para>经 <c>uinput</c> 排队一次双键组合：两个键在同一条命令中按下并抬起。</para></summary>
    /// <param name="modifierKeyCode">Chord modifier, sent first. <para>组合键的修饰键，最先发送。</para></param>
    /// <param name="keyCode">Chord key released after the modifier. <para>在修饰键之后释放的组合键按键。</para></param>
    void SendKeyChord(int modifierKeyCode, int keyCode);

    /// <summary>Queues one UTF-8 text injection through <c>uinput</c>.
    /// <para>经 <c>uinput</c> 排队一次 UTF-8 文本注入。</para></summary>
    void SendText(string text);
}

/// <summary>Capture parameters passed to the pushed device library.
/// <para>传给推送设备库的采集参数。</para>
/// </summary>
/// <param name="FrameRate">Encoder frame rate, 1 through 120. <para>编码帧率，1 至 120。</para></param>
/// <param name="BitRate">Target bitrate in bits per second, 1 through 50 Mbps.
/// <para>目标码率（比特/秒），1 至 50 Mbps。</para></param>
/// <param name="KeyFrameIntervalMs">Interval between forced key frames, in milliseconds.
/// <para>强制关键帧之间的间隔（毫秒）。</para></param>
public sealed record OhscrcpyLaunchOptions(
    int FrameRate = OhscrcpyConstants.DefaultFrameRate,
    int BitRate = OhscrcpyConstants.DefaultBitRate,
    int KeyFrameIntervalMs = OhscrcpyConstants.DefaultKeyFrameIntervalMs)
{
    internal void Validate()
    {
        if (FrameRate is < OhscrcpyConstants.MinimumFrameRate or > OhscrcpyConstants.MaximumFrameRate)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameRate));
        }

        if (BitRate is < OhscrcpyConstants.MinimumBitRate or > OhscrcpyConstants.MaximumBitRate)
        {
            throw new ArgumentOutOfRangeException(nameof(BitRate));
        }

        if (KeyFrameIntervalMs is < 100 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(KeyFrameIntervalMs));
        }
    }
}

/// <summary>UI Test capabilities probed from one device. <para>从单台设备探测到的 UI Test 能力。</para>
/// </summary>
/// <param name="UiTestVersion">Four-component UI Test version. <para>四段式 UI Test 版本。</para></param>
/// <param name="Architecture">Device <c>uname -m</c> text. <para>设备 <c>uname -m</c> 文本。</para></param>
/// <param name="Model">Device product model. <para>设备产品型号。</para></param>
internal sealed record UiTestDeviceInfo(Version UiTestVersion, string Architecture, string Model)
{
    internal bool UsesUnixSockets => UiTestVersion > OhscrcpyConstants.UnixSocketMinimumVersion;

    internal string VideoEndpoint => UsesUnixSockets
        ? OhscrcpyConstants.UnixVideoEndpoint
        : OhscrcpyConstants.LegacyVideoEndpoint;

    internal string ControlEndpoint => UsesUnixSockets
        ? OhscrcpyConstants.UnixControlEndpoint
        : OhscrcpyConstants.LegacyControlEndpoint;
}

/// <summary>Owned UI Test control daemon state that must be cleaned up with the session.
/// <para>随会话一同清理的、由本会话持有的 UI Test 控制守护进程状态。</para>
/// </summary>
/// <param name="OwnedProcessIds">Daemon process ids started by this session. <para>本会话启动的守护进程号。</para></param>
/// <param name="InstalledAgent">Whether this session replaced the device control agent.
/// <para>本会话是否替换了设备控制代理。</para></param>
/// <param name="RemoteAgentPath">Device path of the control agent. <para>控制代理的设备路径。</para></param>
/// <param name="BackupPath">Device path of the replaced agent, or an empty string.
/// <para>被替换代理的设备路径；未替换时为空串。</para></param>
internal sealed record ControlDaemonHandle(
    IReadOnlyList<int> OwnedProcessIds,
    bool InstalledAgent,
    string RemoteAgentPath,
    string BackupPath);

/// <summary>Owned video extension state that must be cleaned up with the session.
/// <para>随会话一同清理的、由本会话持有的视频扩展状态。</para>
/// </summary>
/// <param name="ExtensionName">Pushed extension file name. <para>推送的扩展文件名。</para></param>
/// <param name="RemotePath">Device path of the pushed library. <para>推送库的设备路径。</para></param>
/// <param name="RemoteLogPath">Device path of the daemon log. <para>守护进程日志的设备路径。</para></param>
/// <param name="ProcessIds">Extension process ids started by this session.
/// <para>本会话启动的扩展进程号。</para></param>
internal sealed record VideoDaemonHandle(
    string ExtensionName,
    string RemotePath,
    string RemoteLogPath,
    IReadOnlyList<int> ProcessIds);

/// <summary>Device-side setup, version probing and cleanup for the pushed capture path.
/// <para>推送采集路径的设备侧准备、版本探测与清理。</para>
/// </summary>
internal static partial class OhscrcpyServerManager
{
    private static string? runtimeDirectory;

    internal static string RuntimeDirectory
    {
        get => runtimeDirectory ??= Ohscrcpy.OhscrcpyDeviceAssets.RuntimeDirectory;
        set => runtimeDirectory = value;
    }
}