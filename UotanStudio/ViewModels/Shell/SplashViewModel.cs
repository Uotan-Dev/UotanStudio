using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>启动窗视图模型：显示应用启动阶段的状态文本（USB 扫描、界面准备等）。</para>
/// Splash view model: shows the status text of startup phases (USB scan, UI preparation, etc.).
/// </summary>
/// <remarks>
/// <para>构造启动窗视图模型并填入初始状态文本。</para>
/// Constructs the splash view model with the initial status text.
/// </remarks>
/// <param name="initialStatus">The initial status text. <para>初始状态文本。</para></param>
public partial class SplashViewModel(string initialStatus) : ViewModelBase
{
    /// <summary>
    /// <para>状态文本后备字段。</para>
    /// Status text backing field.
    /// </summary>
    [ObservableProperty]
    private string _statusText = initialStatus;

    /// <summary>Whether startup work is still in progress.</summary>
    [ObservableProperty]
    private bool _isInitializing = true;
}