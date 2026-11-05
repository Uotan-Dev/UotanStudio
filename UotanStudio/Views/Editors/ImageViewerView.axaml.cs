using Avalonia.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// Image viewer document view; all state is supplied by <c>ImageViewerViewModel</c>.
/// <para>图片查看器文档视图；全部状态由 <c>ImageViewerViewModel</c> 提供。</para>
/// </summary>
public partial class ImageViewerView : UserControl
{
    /// <summary>
    /// Creates the view and loads its compiled XAML.
    /// <para>创建视图并载入编译后的 XAML。</para>
    /// </summary>
    public ImageViewerView()
    {
        InitializeComponent();
    }
}