using Avalonia.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// Logo partition workbench view; all state is supplied by <c>LogoPartitionViewModel</c>.
/// <para>Logo 分区工作台视图；全部状态由 <c>LogoPartitionViewModel</c> 提供。</para>
/// </summary>
public partial class LogoPartitionView : UserControl
{
    /// <summary>
    /// Creates the view and loads its compiled XAML.
    /// <para>创建视图并载入编译后的 XAML。</para>
    /// </summary>
    public LogoPartitionView()
    {
        InitializeComponent();
    }
}