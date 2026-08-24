using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UotanStudio.Views;

/// <summary>
/// <para>关于对话框，展示应用程序的版本与简介信息。</para>
/// About dialog, showing application version and description.
/// </summary>
public partial class AboutDialog : Window
{
    /// <summary>
    /// <para>初始化关于对话框的新实例。</para>
    /// Initializes a new instance of the AboutDialog.
    /// </summary>
    public AboutDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <para>关闭按钮点击事件处理器。</para>
    /// Close button click event handler.
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}