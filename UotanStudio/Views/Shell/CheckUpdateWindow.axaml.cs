using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.Services;

namespace UotanStudio.Views.Shell;

/// <summary>Application update check window displaying release notes, version metadata, and download progress.
/// <para>检查更新窗口，用于显示更新日志、版本信息与下载进度。</para></summary>
public partial class CheckUpdateWindow : Window
{
    /// <summary>Initializes a new instance of the <see cref="CheckUpdateWindow"/> class.
    /// <para>初始化 <see cref="CheckUpdateWindow"/> 类的新实例。</para></summary>
    public CheckUpdateWindow()
    {
        InitializeComponent();
        VersionInfoText.Text = LanguageService.Instance.GetString("CheckUpdate_VersionAndSize", "v0.0.0", "0 MB");
    }

    /// <summary>Populates the release notes and version summary shown before updating.
    /// <para>填充更新前展示的更新日志与版本摘要。</para></summary>
    /// <param name="version">Target version string. <para>目标版本号文本。</para></param>
    /// <param name="sizeText">Formatted package size text. <para>格式化后的安装包大小文本。</para></param>
    /// <param name="releaseNotes">Multi-line release notes text. <para>多行更新日志文本。</para></param>
    public void SetUpdateInfo(string version, string sizeText, string releaseNotes)
    {
        VersionInfoText.Text = LanguageService.Instance.GetString("CheckUpdate_VersionAndSize", version, sizeText);
        ReleaseNotesBox.Text = releaseNotes;
    }

    /// <summary>Switches the footer status area to progress mode and updates progress, speed, and remaining time.
    /// <para>将底部状态区域切换为进度条模式，并更新进度、速度与剩余时间。</para></summary>
    /// <param name="progressFraction">Normalized progress in [0, 1]. <para>归一化进度值（0 到 1）。</para></param>
    /// <param name="bytesPerSecond">Current transfer speed in bytes per second. <para>当前下载速度（字节/秒）。</para></param>
    /// <param name="remainingBytes">Remaining bytes to download. <para>剩余待下载字节数。</para></param>
    public void SetDownloadProgress(double progressFraction, double bytesPerSecond, double remainingBytes)
    {
        VersionInfoText.IsVisible = false;
        ProgressPanel.IsVisible = true;
        DownloadProgressBar.Value = Math.Clamp(progressFraction * 100d, 0d, 100d);

        var status = LanguageService.Instance.GetString("CheckUpdate_Downloading");
        if (bytesPerSecond > 0)
        {
            var stats = FormatDownloadStats(remainingBytes, bytesPerSecond);
            ProgressText.Text = $"{status} · {stats}";
        }
        else
        {
            ProgressText.Text = status;
        }
    }

    private void OnUpdateAndRestartClick(object? sender, RoutedEventArgs e)
    {
        VersionInfoText.IsVisible = false;
        ProgressPanel.IsVisible = true;
        ProgressText.Text = LanguageService.Instance.GetString("CheckUpdate_Downloading");
        DownloadProgressBar.Value = 0;
        UpdateAndRestartButton.IsEnabled = false;
        SkipVersionButton.IsEnabled = false;
    }

    private void OnSkipVersionClick(object? sender, RoutedEventArgs e) => Close();

    private void OnRemindLaterClick(object? sender, RoutedEventArgs e) => Close();

    private static string FormatDownloadStats(double remainingBytes, double rate)
    {
        var stats = $"{FormatBytes(rate)}/s";
        if (remainingBytes <= 0)
        {
            return stats;
        }

        var eta = TimeSpan.FromSeconds(Math.Ceiling(remainingBytes / rate));
        var remaining = LanguageService.Instance.GetString("CheckUpdate_TimeRemaining",
            eta.TotalHours >= 1 ? eta.ToString(@"h\:mm") : eta.ToString(@"mm\:ss"));
        return $"{stats} · {remaining}";
    }

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (1L << 10):F1} KB",
        _ => $"{bytes:F0} B",
    };
}