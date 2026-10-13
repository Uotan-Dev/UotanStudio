using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.Services;

namespace UotanStudio.Views.Shell;

/// <summary>
/// About dialog, showing the application version and description, and expanding the open-source
/// credits section downwards on demand.
/// <para>关于对话框，展示应用程序的版本与简介信息，并可按需向下展开开源项目区块。</para>
/// </summary>
public partial class AboutDialog : Window
{
    /// <summary>
    /// Initializes a new instance of the AboutDialog.
    /// <para>初始化关于对话框的新实例。</para>
    /// </summary>
    public AboutDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Expands or collapses the open-source section; the window grows downwards with its content.
    /// <para>展开或收起开源项目区块；窗口高度随内容向下扩张。</para>
    /// </summary>
    /// <param name="sender">The event sender. <para>事件发送者。</para></param>
    /// <param name="e">The event arguments. <para>事件参数。</para></param>
    private void OnOpenSourceClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<StackPanel>("OpenSourcePanel") is { } panel)
        {
            panel.IsVisible = !panel.IsVisible;
        }
    }

    /// <summary>
    /// Closes the About dialog.
    /// <para>关闭关于对话框。</para>
    /// </summary>
    /// <param name="sender">The event sender. <para>事件发送者。</para></param>
    /// <param name="e">The event arguments. <para>事件参数。</para></param>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Opens a link in the system default browser (repository/author link buttons in the
    /// open-source section).
    /// <para>在系统默认浏览器中打开链接（开源项目区块的仓库/作者链接按钮）。</para>
    /// </summary>
    /// <param name="sender">The event sender. <para>事件发送者。</para></param>
    /// <param name="e">The event arguments. <para>事件参数。</para></param>
    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            ExternalLinkService.TryOpen(url);
        }
    }
}