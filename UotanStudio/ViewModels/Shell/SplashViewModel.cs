using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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

    private SplashPhase _phase;

    /// <summary>
    /// Font family used by the splash before the bundled MiSans frames are decompressed: the platform default, resolved
    /// without reaching for the <c>fonts:</c> collection that is still empty at that point.
    /// <para>内嵌 MiSans 解压前启动窗使用的字体族：平台默认字体，解析时不触碰此时仍为空的 <c>fonts:</c> 集合。</para>
    /// </summary>
    /// <value>A system font family:<para>系统字体族：</para> <see cref="FontFamily"/>.</value>
    public FontFamily SplashFontFamily { get; } = ResolveSystemFontFamily();

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

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static string Text(string key) => LanguageService.Instance.GetString(key);

    private static FontFamily ResolveSystemFontFamily()
    {
        return FontManager.Current.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
            null, CultureInfo.CurrentCulture, out var typeface)
            ? typeface.FontFamily
            : FontFamily.Default;
    }
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