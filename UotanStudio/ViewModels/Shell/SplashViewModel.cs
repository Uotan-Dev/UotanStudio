using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Splash view model: shows the status text of startup phases (USB scan, UI preparation, etc.).
/// <para>启动窗视图模型：显示应用启动阶段的状态文本（USB 扫描、界面准备等）。</para>
/// </summary>
/// <remarks>
/// Constructs the splash view model with the initial status text.
/// <para>构造启动窗视图模型并填入初始状态文本。</para>
/// </remarks>
/// <param name="initialStatus">The initial status text. <para>初始状态文本。</para></param>
public partial class SplashViewModel(string initialStatus) : ViewModelBase
{
    /// <summary>
    /// Status text backing field.
    /// <para>状态文本后备字段。</para>
    /// </summary>
    [ObservableProperty]
    private string _statusText = initialStatus;

    private SplashPhase _phase;

    public bool IsInitializing => _phase != SplashPhase.Ready;

    /// <summary>Publishes the next stage before allowing synchronous UI construction to continue.</summary>
    internal async Task AdvanceAsync(SplashPhase phase)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (phase < _phase || !Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
        _phase = phase;
        OnPropertyChanged(nameof(IsInitializing));
        StatusText = LanguageService.Instance.GetString(phase switch
        {
            SplashPhase.Settings => "Splash_Settings",
            SplashPhase.ApplyingSettings => "Splash_ApplyingSettings",
            SplashPhase.Scanning => "Splash_Scanning",
            SplashPhase.Services => "Splash_Services",
            SplashPhase.Nodes => "Splash_Nodes",
            SplashPhase.Workspace => "Splash_Workspace",
            SplashPhase.Window => "Splash_Preparing",
            SplashPhase.OpeningFiles => "Splash_OpeningFiles",
            SplashPhase.Ready => "Splash_Ready",
            _ => "Splash_Initializing"
        });

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
}

internal enum SplashPhase
{
    Initializing,
    Settings,
    ApplyingSettings,
    Scanning,
    Services,
    Nodes,
    Workspace,
    Window,
    OpeningFiles,
    Ready
}