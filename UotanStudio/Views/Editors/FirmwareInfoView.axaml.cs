using Avalonia.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>固件元数据面板视图：只读展示固件文件的格式名、摘要、元数据键值对与条目列表。</para>
/// Firmware metadata panel view: read-only display of a firmware file's format name, summary,
/// metadata key-value pairs and entry list.
/// </summary>
public partial class FirmwareInfoView : UserControl
{
    /// <summary>
    /// <para>创建固件元数据面板视图并初始化组件。</para>
    /// Creates the firmware metadata panel view and initializes its components.
    /// </summary>
    public FirmwareInfoView()
    {
        InitializeComponent();
    }
}