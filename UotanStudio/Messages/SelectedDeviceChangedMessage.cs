using UotanStudio.Services.Devices;

namespace UotanStudio.Messages;

/// <summary>
/// Reports that the device list selection changed. Mirror documents do not retarget or focus on this
/// message; they are opened per device by an explicit <see cref="OpenScreenMirrorRequestMessage"/>.
/// A null protocol means the selection was cleared.
/// <para>报告设备列表的选中项变化。投屏文档不会因此切换目标或聚焦，只由显式的
/// <see cref="OpenScreenMirrorRequestMessage"/> 按设备打开。协议为 null 表示选择被清空。</para>
/// </summary>
public sealed class SelectedDeviceChangedMessage
{
    /// <summary>Protocol of the selected endpoint, or null when nothing is selected.
    /// <para>所选端点的协议；未选中任何设备时为 null。</para></summary>
    public DeviceProtocolKind? Protocol { get; init; }

    /// <summary>Serial or connect key of the selected endpoint, or null when unknown.
    /// <para>所选端点的序列号或连接键；未知时为 null。</para></summary>
    public string? Serial { get; init; }
}