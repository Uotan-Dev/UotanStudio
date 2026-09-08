namespace UotanStudio.Messages;

/// <summary>
/// <para>请求对指定设备开始 scrcpy 投屏。</para>
/// Requests starting scrcpy mirroring for a specific device.
/// </summary>
public sealed class OpenScreenMirrorRequestMessage
{
    /// <summary><para>目标设备的序列号。</para>The target device serial.</summary>
    public required string Serial { get; init; }
}
