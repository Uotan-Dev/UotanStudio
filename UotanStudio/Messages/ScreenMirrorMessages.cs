namespace UotanStudio.Messages;

/// <summary>
/// Requests starting screen mirroring for a specific device, or opening the screen-mirror page when null.
/// <para>请求对指定设备开始投屏；为 null 时打开投屏页面。</para>
/// </summary>
public sealed class OpenScreenMirrorRequestMessage
{
    /// <summary>The target device serial, or null when opening the document without an immediate session.<para>目标设备的序列号；不指定设备或无可用设备时为 null。</para></summary>
    /// <value>The serial identifying the target device, or null.<para>用于标识目标设备的序列号，或为 null。</para></value>
    public string? Serial { get; init; }
}