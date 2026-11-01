using System.Collections.ObjectModel;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// A read-only name and value displayed in the flash page device status table.
/// <para>烧录页面设备状态表格中的只读名称和值。</para>
/// </summary>
public sealed class FlashDeviceStatusEntry
{
    /// <summary>Display name of the status item.<para>状态项的显示名称。</para></summary>
    public required string Name { get; init; }

    /// <summary>Display value of the status item.<para>状态项的显示值。</para></summary>
    public required string Value { get; init; }
}

public sealed partial class FlashViewModel
{
    /// <summary>
    /// Status rows displayed by the view; empty until supplied by the caller.
    /// <para>视图显示的状态行；调用方提供数据前为空。</para>
    /// </summary>
    public ObservableCollection<FlashDeviceStatusEntry> DeviceStatus { get; } = [];
}