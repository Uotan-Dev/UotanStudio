using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>Application update check window: release notes, version summary and download progress.
/// <para>检查更新窗口：显示更新日志、版本摘要与下载进度。</para></summary>
/// <remarks>The window only forwards the footer actions to <see cref="CheckUpdateViewModel"/> and starts the
/// check once it is shown; every network and installer decision stays in the view model.
/// <para>窗口只把底部动作转交给 <see cref="CheckUpdateViewModel"/>，并在显示后启动检查；所有网络与安装决策
/// 都留在视图模型里。</para></remarks>
public partial class CheckUpdateWindow : Window
{
    private bool _started;

    /// <summary>Initializes a new instance of the <see cref="CheckUpdateWindow"/> class.
    /// <para>初始化 <see cref="CheckUpdateWindow"/> 类的新实例。</para></summary>
    public CheckUpdateWindow()
    {
        InitializeComponent();
        DataContext = ServiceHub.TryGet<CheckUpdateViewModel>() ?? CreateStandaloneViewModel();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private CheckUpdateViewModel ViewModel => (CheckUpdateViewModel)DataContext!;

    /// <summary>
    /// Creates a view model when the container is unavailable, so the window still works in tests that do
    /// not start the composition root.
    /// <para>容器不可用时创建视图模型，使未启动组合根的测试中窗口仍可用。</para>
    /// </summary>
    /// <returns>The standalone view model. <para>独立构建的视图模型。</para></returns>
    private static CheckUpdateViewModel CreateStandaloneViewModel()
        => new(new Services.Update.AppUpdateService(Services.Network.SharedHttpClient.Shared),
            LanguageService.Instance);

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (!ViewModel.HasResult)
        {
            await ViewModel.CheckAsync();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.Cancel();
        Opened -= OnOpened;
        Closed -= OnClosed;
    }

    private async void OnUpdateAndRestartClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.UpdateAsync(ExitApplicationAsync);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Update", $"Applying the update failed: {ex.Message}");
        }
    }

    private void OnSkipVersionClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.SkipVersion();
        Close();
    }

    private void OnRemindLaterClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Shuts the application down so the platform installer can replace the running bundle; used when the
    /// platform reports that the application must exit first.
    /// <para>关闭应用以便平台安装器替换正在运行的应用包；平台上报需要先退出时使用。</para>
    /// </summary>
    /// <returns>A completed task once shutdown was requested. <para>请求关闭后完成的任务。</para></returns>
    private Task ExitApplicationAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }

        return Task.CompletedTask;
    }
}