using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.Services;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>关于对话框，展示应用程序的版本与简介信息，并可按需向下展开开源项目区块。</para>
/// About dialog, showing the application version and description, and expanding the open-source
/// credits section downwards on demand.
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
    /// <para>展开或收起开源项目区块；窗口高度随内容向下扩张。</para>
    /// Expands or collapses the open-source section; the window grows downwards with its content.
    /// </summary>
    /// <param name="sender"><para>事件发送者。</para>The event sender.</param>
    /// <param name="e"><para>事件参数。</para>The event arguments.</param>
    private void OnOpenSourceClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<StackPanel>("OpenSourcePanel") is { } panel)
        {
            panel.IsVisible = !panel.IsVisible;
        }
    }

    /// <summary>
    /// <para>关闭关于对话框。</para>
    /// Closes the About dialog.
    /// </summary>
    /// <param name="sender"><para>事件发送者。</para>The event sender.</param>
    /// <param name="e"><para>事件参数。</para>The event arguments.</param>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// <para>在系统默认浏览器中打开链接（开源项目区块的仓库/作者链接按钮）。</para>
    /// Opens a link in the system default browser (repository/author link buttons in the
    /// open-source section).
    /// </summary>
    /// <param name="sender"><para>事件发送者。</para>The event sender.</param>
    /// <param name="e"><para>事件参数。</para>The event arguments.</param>
    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            ExternalLinkService.TryOpen(url);
        }
    }
}