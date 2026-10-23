using UotanStudio.Services.Devices;

namespace UotanStudio.Messages;

/// <summary>
/// Reports that the device list selection changed, so mirror documents can retarget or show the
/// protocol-specific unsupported notice. A null protocol means the selection was cleared.
/// <para>报告设备列表的选中项变化，使投屏文档能够切换目标或显示协议特定的不支持提示。协议为 null 表示选择被清空。</para>
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