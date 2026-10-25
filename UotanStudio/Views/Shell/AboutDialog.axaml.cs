using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using UotanStudio.Build;
using UotanStudio.Services;

namespace UotanStudio.Views.Shell;

/// <summary>
/// About dialog, showing the application version, distribution channel and compiled commit, and
/// expanding the open-source credits section downwards on demand.
/// <para>关于对话框，展示应用程序版本、发行通道与编译提交，并可按需向下展开开源项目区块。</para>
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
        ApplyBuildInfo();
    }

    /// <summary>
    /// Fills the version, the two channel badges and the commit link from the compiled build
    /// metadata.
    /// <para>从编译期构建元数据填充版本、两个通道徽标与提交链接。</para>
    /// </summary>
    private void ApplyBuildInfo()
    {
        var language = LanguageService.Instance;
        VersionText.Text = string.Concat(language.GetString("About_Version"), " ", BuildInfo.Version);

        var badges = new (BuildChannel Channel, Border Badge, TextBlock Label)[]
        {
            (BuildChannel.Release, ReleaseBadge, ReleaseText),
            (BuildChannel.Beta, BetaBadge, BetaText),
        };
        foreach (var (channel, badge, label) in badges)
        {
            var isCurrent = channel == BuildInfo.Channel;
            label.Text = language.GetString(BuildInfo.GetChannelKey(channel));
            label.FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal;
            badge.Classes.Clear();
            if (isCurrent)
            {
                badge.Classes.Add(channel == BuildChannel.Release ? "ReleaseCurrent" : "BetaCurrent");
            }
            else
            {
                badge.Classes.Add("ChannelInactive");
            }

            badge.BorderThickness = new Thickness(1);
            ToolTip.SetTip(badge, string.Concat(
                channel.ToString().ToLowerInvariant(),
                " — ",
                language.GetString(BuildInfo.GetChannelDescriptionKey(channel))));
        }

        if (BuildInfo.HasCommit)
        {
            CommitLink.Content = BuildInfo.CommitShort;
            CommitLink.Tag = BuildInfo.CommitUrl;
            ToolTip.SetTip(CommitLink, language.GetString("About_OpenCommit"));
        }
        else
        {
            CommitLink.IsVisible = false;
            CommitUnavailableText.IsVisible = true;
        }
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
    /// Opens the compiled commit page in the system default browser.
    /// <para>在系统默认浏览器中打开编译提交页面。</para>
    /// </summary>
    /// <param name="sender">The event sender. <para>事件发送者。</para></param>
    /// <param name="e">The event arguments. <para>事件参数。</para></param>
    private void OnCommitClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            ExternalLinkService.TryOpen(url);
        }
    }

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
