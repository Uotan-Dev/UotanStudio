using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Fonts;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Splash view model: shows the status text of startup phases (resource decompression, settings, device scan, UI
/// preparation, etc.).
/// <para>启动窗视图模型：显示应用启动阶段的状态文本（资源解压、设置、设备扫描、界面准备等）。</para>
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

    /// <summary>
    /// Target fill ratio for the splash progress bar, in <c>0..1</c>. It advances monotonically with
    /// <see cref="SplashPhase"/>; the view interpolates toward it with a compositor animation so discrete
    /// phase steps never appear as length jumps.
    /// <para>启动进度条的目标填充比例，取值 <c>0..1</c>。随 <see cref="SplashPhase"/> 单调前进；视图用合成动画
    /// 向其插值，使离散阶段不会表现为长度突变。</para>
    /// </summary>
    [ObservableProperty]
    private double _targetProgress;

    private SplashPhase _phase;

    /// <summary>
    /// Font family used by the splash: the bundled MiSans collection, which the host registers before this window
    /// is created. No system family is requested, because the Linux build links a Skia without fontconfig and
    /// therefore cannot resolve one.
    /// <para>启动窗使用的字体族：宿主在创建本窗口前注册的随包 MiSans 集合。不请求系统字族，因为 Linux 构建链接的
    /// Skia 不带 fontconfig，无法解析系统字体。</para>
    /// </summary>
    /// <value>The <c>fonts:UotanStudio/MiSansVF#MiSans VF</c> family.<para><c>fonts:UotanStudio/MiSansVF#MiSans VF</c> 字族。</para></value>
    public FontFamily SplashFontFamily { get; } = new(BundledFont.Reference);

    /// <summary>Publishes the next stage before allowing synchronous UI construction to continue.</summary>
    /// <param name="phase">The stage to publish; it must not move backwards.
    /// <para>要发布的阶段；不允许倒退。</para></param>
    /// <param name="deviceCount">Device count for <see cref="SplashPhase.DevicesFound"/>; zero or negative shows the
    /// "no devices" text. <para><see cref="SplashPhase.DevicesFound"/> 的设备数量；零或负值显示“未发现设备”文本。</para></param>
    internal async Task AdvanceAsync(SplashPhase phase, int deviceCount = -1)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (phase < _phase || !Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
        _phase = phase;
        StatusText = phase switch
        {
            SplashPhase.Resources => Text("Splash_Resources"),
            SplashPhase.Settings => Text("Splash_Settings"),
            SplashPhase.ApplyingSettings => Text("Splash_ApplyingSettings"),
            SplashPhase.Scanning => Text("Splash_Scanning"),
            SplashPhase.DevicesFound => deviceCount > 0
                ? string.Format(CultureInfo.CurrentCulture, Text("Splash_FoundDevices"), deviceCount)
                : Text("Splash_NoDevices"),
            SplashPhase.Services => Text("Splash_Services"),
            SplashPhase.Nodes => Text("Splash_Nodes"),
            SplashPhase.Workspace => Text("Splash_Workspace"),
            SplashPhase.Window => Text("Splash_Preparing"),
            SplashPhase.OpeningFiles => Text("Splash_OpeningFiles"),
            SplashPhase.Ready => Text("Splash_Ready"),
            _ => Text("Splash_Initializing")
        };
        TargetProgress = (int)phase / (double)SplashPhase.Ready;

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static string Text(string key) => LanguageService.Instance.GetString(key);
}

internal enum SplashPhase
{
    Initializing,
    Resources,
    Settings,
    ApplyingSettings,
    Scanning,
    DevicesFound,
    Services,
    Nodes,
    Workspace,
    Window,
    OpeningFiles,
    Ready
}