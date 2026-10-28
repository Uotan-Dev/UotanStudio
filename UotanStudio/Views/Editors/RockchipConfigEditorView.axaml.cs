using Avalonia.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// Read-only browser for an RKDevTool download-list configuration; the view model owns parsing and the
/// view only hosts the list and the trailing-block pane.
/// <para>RKDevTool 下载列表配置的只读浏览视图；解析由视图模型负责，本视图只承载列表与尾块面板。</para>
/// </summary>
public partial class RockchipConfigEditorView : UserControl
{
    /// <summary>
    /// Creates the configuration browser view.
    /// <para>创建配置浏览视图。</para>
    /// </summary>
    public RockchipConfigEditorView() => InitializeComponent();
}