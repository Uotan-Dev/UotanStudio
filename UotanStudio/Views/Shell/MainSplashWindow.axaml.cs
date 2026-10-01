using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>Ursa 主题启动窗：应用启动期间显示状态（首次 USB 扫描等），关闭时过渡到主窗口。</para>
/// Ursa-themed splash window: shows startup status (initial USB scan, etc.) and transitions
/// to the main window when closed.
/// </summary>
public partial class MainSplashWindow : SplashWindow
{
    /// <summary>
    /// <para>启动序列任务；<see cref="CanClose"/> 会等待其完成，保证用户提前关闭启动窗时初始化不被打断。</para>
    /// The startup sequence task; <see cref="CanClose"/> awaits it so an early user close
    /// never interrupts initialization.
    /// </summary>
    internal Task? StartupTask { get; set; }

    /// <summary>
    /// <para>启动序列期间预构建的主窗口；<see cref="CreateNextWindow"/> 优先返回它，
    /// 使扫描结束到主窗显示的过渡不卡顿。</para>
    /// Main window pre-built during the startup sequence; <see cref="CreateNextWindow"/>
    /// returns it first so the transition from splash to main window is instant.
    /// </summary>
    internal Window? PreparedMainWindow { get; set; }

    /// <summary>
    /// <para>初始化启动窗。</para>
    /// Initializes the splash window.
    /// </summary>
    public MainSplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>Waits for the completed progress state to reach the render thread before closing.</summary>
    internal async Task PresentCompletionAsync()
    {
        UpdateLayout();
        if (IsVisible && ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
        {
            var batch = compositor.RequestCompositionBatchCommitAsync();
            try
            {
                await batch.Rendered.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {

            }
        }
    }

    /// <summary>
    /// <para>等待启动序列完成后允许关闭。</para>
    /// Allows closing only after the startup sequence has completed.
    /// </summary>
    protected override async Task<bool> CanClose()
    {
        if (StartupTask is not null)
        {
            await StartupTask.ConfigureAwait(true);
        }
        return true;
    }

    /// <summary>
    /// <para>创建并返回主窗口（由 Ursa SplashWindow 在关闭时调用）；优先返回启动序列中
    /// 预构建的实例，避免过渡到主窗体时卡顿。</para>
    /// Creates and returns the main window (called by the Ursa SplashWindow on close);
    /// prefers the instance pre-built during the startup sequence to avoid transition jank.
    /// </summary>
    protected override Task<Window?> CreateNextWindow()
        => Task.FromResult<Window?>(PreparedMainWindow ?? new MainWindow());
}