using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Build;
using UotanStudio.Services;
using UotanStudio.Services.Update;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// The stage the check-update window is showing.
/// <para>检查更新窗口当前展示的阶段。</para>
/// </summary>
public enum CheckUpdateStage
{
    /// <summary>Contacting the release feed. <para>正在连接 Release 源。</para></summary>
    Checking,

    /// <summary>The running build is current. <para>运行中的构建已是最新。</para></summary>
    UpToDate,

    /// <summary>A newer release with a usable package was found. <para>找到了带有可用包的新版本。</para></summary>
    UpdateAvailable,

    /// <summary>The package is downloading. <para>正在下载安装包。</para></summary>
    Downloading,

    /// <summary>The package is verified and ready to install. <para>安装包已校验，可以安装。</para></summary>
    Ready,

    /// <summary>The check or the download failed. <para>检查或下载失败。</para></summary>
    Failed,
}

/// <summary>
/// View model of the check-update window: it queries the release feed, reports the newest version, and
/// downloads and hands the package to the platform installer.
/// <para>检查更新窗口的视图模型：查询 Release 源、报告最新版本，并下载安装包后交给平台安装器。</para>
/// </summary>
/// <remarks>
/// The window itself stays free of network and installer logic; every operation runs through this model so
/// the checks are testable without a UI. The three footer actions mirror the platform flow: skip records the
/// version so it is never offered again, remind-later only closes, and update downloads then launches.
/// <para>窗口本身不承担网络与安装逻辑；所有操作都经由本模型，使检查可在没有界面的情况下测试。底部三个动作对应
/// 平台流程：跳过会记录版本使其不再提示，以后提醒只关闭窗口，更新则下载后启动安装。</para>
/// </remarks>
public sealed partial class CheckUpdateViewModel : ViewModelBase
{
    private readonly AppUpdateService _service;
    private readonly ILocalizer _localizer;
    private AppUpdateCheckResult? _result;
    private CancellationTokenSource? _operation;

    /// <summary>
    /// Creates the view model over the update service and the localized string source.
    /// <para>围绕更新服务与本地化字符串来源创建视图模型。</para>
    /// </summary>
    /// <param name="service">The update service performing checks and downloads. <para>执行检查与下载的更新服务。</para></param>
    /// <param name="localizer">Localized string lookup. <para>本地化字符串查询。</para></param>
    public CheckUpdateViewModel(AppUpdateService service, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>The current stage shown by the window. <para>窗口当前展示的阶段。</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanUpdate), nameof(CanSkip), nameof(ShowsProgress),
        nameof(ShowsReleaseNotes), nameof(ShowsFooterInfo))]
    private CheckUpdateStage _stage = CheckUpdateStage.Checking;

    /// <summary>The newest release version text. <para>最新 Release 的版本文本。</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionSummary))]
    private string _latestVersion = string.Empty;

    /// <summary>The running build's version text. <para>运行中构建的版本文本。</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionSummary))]
    private string _currentVersion = BuildInfo.Version;

    /// <summary>The formatted package size, or an empty string when unknown. <para>格式化后的安装包大小；未知时为空字符串。</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionSummary))]
    private string _packageSize = string.Empty;

    /// <summary>The release notes shown in the window body. <para>窗口正文中展示的更新日志。</para></summary>
    [ObservableProperty]
    private string _releaseNotes = string.Empty;

    /// <summary>Progress in the range 0..1. <para>0 到 1 之间的进度。</para></summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>The status line shown above the progress bar. <para>进度条上方展示的状态行。</para></summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Whether an operation is currently running. <para>当前是否有操作在运行。</para></summary>
    public bool IsBusy => Stage is CheckUpdateStage.Checking or CheckUpdateStage.Downloading;

    /// <summary>Whether the footer offers the update action. <para>底部是否提供更新动作。</para></summary>
    public bool CanUpdate => Stage is CheckUpdateStage.UpdateAvailable or CheckUpdateStage.Ready;

    /// <summary>Whether the footer offers the skip action. <para>底部是否提供跳过动作。</para></summary>
    public bool CanSkip => Stage is CheckUpdateStage.UpdateAvailable or CheckUpdateStage.Ready;

    /// <summary>Whether the progress bar is visible. <para>进度条是否可见。</para></summary>
    public bool ShowsProgress => Stage is CheckUpdateStage.Downloading or CheckUpdateStage.Ready;

    /// <summary>Whether the release notes body is visible. <para>更新日志正文是否可见。</para></summary>
    public bool ShowsReleaseNotes => Stage is not CheckUpdateStage.Checking;

    /// <summary>Whether the version summary line is visible. <para>版本摘要行是否可见。</para></summary>
    public bool ShowsFooterInfo => Stage is not CheckUpdateStage.Checking;

    /// <summary>The localized "new version · size" line. <para>本地化的「新版本 · 大小」行。</para></summary>
    public string VersionSummary => string.IsNullOrEmpty(LatestVersion)
        ? string.Empty
        : _localizer.GetString("CheckUpdate_VersionAndSize", LatestVersion,
            string.IsNullOrEmpty(PackageSize) ? _localizer.GetString("CheckUpdate_SizeUnknown") : PackageSize);

    /// <summary>
    /// Applies an already completed check so the window repaints without contacting the feed again, which is
    /// how the startup check opens the window.
    /// <para>套用已完成的一次检查，使窗口无需再次访问源站即可重绘；启动检查便以这种方式打开窗口。</para>
    /// </summary>
    /// <param name="result">The completed check result. <para>已完成的检查结果。</para></param>
    public void ApplyResult(AppUpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        LatestVersion = result.Release.Version;
        PackageSize = result.Package is { Size: > 0 } package ? FormatBytes(package.Size) : string.Empty;
        ReleaseNotes = result.ReleaseNotes ?? string.Empty;
        if (!result.IsNewer || result.Package is null)
        {
            Stage = result.IsNewer ? CheckUpdateStage.Failed : CheckUpdateStage.UpToDate;
            StatusText = result.IsNewer
                ? _localizer.GetString("CheckUpdate_NoPackage")
                : _localizer.GetString("CheckUpdate_UpToDate", CurrentVersion);
            return;
        }

        Stage = CheckUpdateStage.UpdateAvailable;
        StatusText = VersionSummary;
    }

    /// <summary>
    /// Whether a completed check already populated the model, so the window can skip its own request.
    /// <para>是否已由一次检查填充过模型，使窗口可以跳过自身的请求。</para>
    /// </summary>
    /// <value>True when a result is available. <para>已有结果时为真。</para></value>
    public bool HasResult => _result is not null;

    /// <summary>
    /// Discards the previous result so the next window activation queries the feed again; a manual menu
    /// check uses this to force a fresh request instead of repainting the startup result.
    /// <para>丢弃上一次的结果，使窗口下次激活时重新查询源站；菜单手动检查用它强制重新请求，而不是重绘启动结果。</para>
    /// </summary>
    public void ResetForManualCheck()
    {
        CancelRunningOperation();
        _result = null;
        Stage = CheckUpdateStage.Checking;
        StatusText = _localizer.GetString("CheckUpdate_Checking");
        ReleaseNotes = string.Empty;
        Progress = 0;
    }

    /// <summary>
    /// Runs one update check and updates the window state; failures move the model to
    /// <see cref="CheckUpdateStage.Failed"/> instead of throwing at the caller.
    /// <para>执行一次更新检查并更新窗口状态；失败时转入 <see cref="CheckUpdateStage.Failed"/>，不向调用方抛出。</para>
    /// </summary>
    /// <returns>A task that completes when the check finished. <para>检查结束时完成的任务。</para></returns>
    public async Task CheckAsync()
    {
        CancelRunningOperation();
        var operation = new CancellationTokenSource();
        _operation = operation;
        Stage = CheckUpdateStage.Checking;
        StatusText = _localizer.GetString("CheckUpdate_Checking");
        ReleaseNotes = string.Empty;
        try
        {
            var skipped = SettingsPersistenceService.Instance.LoadSettings().SkippedUpdateVersion;
            ApplyResult(await _service.CheckAsync(skipped, operation.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Write(LogLevel.Info, "Update", "The update check was cancelled.");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Update", $"The update check failed: {ex.Message}");
            Stage = CheckUpdateStage.Failed;
            StatusText = _localizer.GetString("CheckUpdate_CheckFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }

            operation.Dispose();
        }
    }

    /// <summary>
    /// Downloads the selected package and hands it to the platform installer.
    /// <para>下载选中的安装包并交给平台安装器。</para>
    /// </summary>
    /// <param name="exitApplication">Shuts the application down when the platform needs it; used by macOS.
    /// <para>平台需要时关闭应用；macOS 会用到。</para></param>
    /// <returns>True when the platform installer was started. <para>平台安装器已启动时为真。</para></returns>
    public async Task<bool> UpdateAsync(Func<Task>? exitApplication = null)
    {
        if (_result?.Package is not { } package)
        {
            return false;
        }

        CancelRunningOperation();
        var operation = new CancellationTokenSource();
        _operation = operation;
        Stage = CheckUpdateStage.Downloading;
        Progress = 0;
        StatusText = _localizer.GetString("CheckUpdate_Downloading");
        var relay = new Progress<AppUpdateDownloadProgress>(sample =>
        {
            Progress = sample.Total > 0 ? Math.Clamp((double)sample.Processed / sample.Total, 0d, 1d) : 0d;
            StatusText = sample.Total > 0
                ? _localizer.GetString("CheckUpdate_DownloadProgress",
                    FormatBytes(sample.Processed), FormatBytes(sample.Total))
                : _localizer.GetString("CheckUpdate_Downloading");
        });
        try
        {
            var path = await _service.DownloadAsync(package, relay, operation.Token).ConfigureAwait(true);
            Stage = CheckUpdateStage.Ready;
            Progress = 1;
            StatusText = _localizer.GetString("CheckUpdate_ReadyToInstall");
            var launch = AppUpdateLauncher.Launch(path, package.Kind);
            if (launch.Outcome != AppUpdateLaunchOutcome.Started)
            {
                StatusText = launch.Detail;
                return false;
            }

            if (launch.RequiresApplicationExit && exitApplication is not null)
            {
                await exitApplication().ConfigureAwait(true);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Write(LogLevel.Info, "Update", "The update download was cancelled.");
            Stage = CheckUpdateStage.UpdateAvailable;
            StatusText = _localizer.GetString("CheckUpdate_DownloadCancelled");
            return false;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Update", $"The update download failed: {ex.Message}");
            Stage = CheckUpdateStage.Failed;
            StatusText = _localizer.GetString("CheckUpdate_DownloadFailed", ex.Message);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }

            operation.Dispose();
        }
    }

    /// <summary>
    /// Records the offered version as skipped so later checks never prompt for it again.
    /// <para>把本次提供的版本记录为已跳过，使后续检查不再提示它。</para>
    /// </summary>
    public void SkipVersion()
    {
        if (string.IsNullOrEmpty(LatestVersion))
        {
            return;
        }

        try
        {
            var settings = SettingsPersistenceService.Instance.LoadSettings();
            settings.SkippedUpdateVersion = LatestVersion;
            if (!SettingsPersistenceService.Instance.TrySaveSettings(settings, out var error))
            {
                LogService.Instance.Write(LogLevel.Warn, "Update", $"Persisting the skipped update failed: {error}");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Update", $"Persisting the skipped update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancels the running check or download; safe to call when nothing is running.
    /// <para>取消正在运行的检查或下载；没有操作运行时调用是安全的。</para>
    /// </summary>
    public void Cancel()
    {
        CancelRunningOperation();
    }

    private void CancelRunningOperation()
    {
        if (_operation is { } operation)
        {
            _operation = null;
            try
            {
                operation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}