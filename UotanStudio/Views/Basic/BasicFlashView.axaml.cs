using Avalonia.Controls;

namespace UotanStudio.Views.Basic;

/// <summary>
/// <para>基础页面的「基本刷入」视图：解锁、Recovery、重启、Boot 修补与简易刷入。</para>
/// The basic page's "basic flash" view: unlock, recovery, reboot, boot patching and easy flash.
/// </summary>
/// <remarks>
/// <para>本视图的按钮尚未接入后端；「修补 Boot」只保留参数展示，修补仍由 Boot 修补窗口执行，
/// 避免在页面里维护第二套修补参数。</para>
/// The buttons here are not wired to a backend yet. The "patch boot" card only shows the parameters; the patch
/// itself still runs in the boot patch window so the page never keeps a second copy of them.
/// </remarks>
public partial class BasicFlashView : UserControl
{
    /// <summary>Initializes the basic-flash section. 初始化「基本刷入」视图。</summary>
    public BasicFlashView() => InitializeComponent();
}