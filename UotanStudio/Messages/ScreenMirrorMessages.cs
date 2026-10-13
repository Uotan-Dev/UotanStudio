namespace UotanStudio.Messages;

/// <summary>
/// Requests starting screen mirroring for a specific device.
/// <para>请求对指定设备开始投屏。</para>
/// </summary>
public sealed class OpenScreenMirrorRequestMessage
{
    /// <summary>The target device serial.<para>目标设备的序列号。</para></summary>
    /// <value>The serial identifying the target device.<para>用于标识目标设备的序列号。</para></value>
    public required string Serial { get; init; }
}