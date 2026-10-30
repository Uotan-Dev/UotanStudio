using System.ComponentModel;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using UotanStudio.ViewModels.Shell;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Ursa-themed splash window: shows startup status (initial USB scan, etc.) and transitions
/// to the main window when closed.
/// <para>Ursa 主题启动窗：应用启动期间显示状态（首次 USB 扫描等），关闭时过渡到主窗口。</para>
/// </summary>
public partial class MainSplashWindow : SplashWindow
{
    /// <summary>
    /// The startup sequence task; <see cref="CanClose"/> awaits it so an early user close
    /// never interrupts initialization.
    /// <para>启动序列任务；<see cref="CanClose"/> 会等待其完成，保证用户提前关闭启动窗时初始化不被打断。</para>
    /// </summary>
    internal Task? StartupTask { get; set; }

    /// <summary>
    /// Main window pre-built during the startup sequence; <see cref="CreateNextWindow"/>
    /// returns it first so the transition from splash to main window is instant.
    /// <para>启动序列期间预构建的主窗口；<see cref="CreateNextWindow"/> 优先返回它，
    /// 使扫描结束到主窗显示的过渡不卡顿。</para>
    /// </summary>
    internal Window? PreparedMainWindow { get; set; }

    /// <summary>
    /// Initializes the splash window.
    /// <para>初始化启动窗。</para>
    /// </summary>
    public MainSplashWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ProgressFill.AttachedToVisualTree += OnFillAttached;
    }

    /// <summary>
    /// Waits for the completed progress state to reach the render thread before closing.
    /// <para>等待完成态进度到达渲染线程后再关闭。</para>
    /// </summary>
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

    private SplashViewModel? _viewModel;
    private bool _fillComposed;
    private bool _fillRevealed;
    private double _currentRatio;
    private double _pendingRatio;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as SplashViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _currentRatio = _viewModel.TargetProgress;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyProgress(_currentRatio);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplashViewModel.TargetProgress) && _viewModel is not null)
        {
            ApplyProgress(_viewModel.TargetProgress);
        }
    }

    private void OnFillAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _fillComposed = true;
        ApplyProgress(_pendingRatio);
    }

    /// <summary>
    /// Tweens the indicator fill toward <paramref name="ratio"/> with a compositor <c>Scale</c> animation, so phase
    /// steps glide instead of snapping. The animation runs on the compositor thread and therefore keeps advancing
    /// while the UI thread is busy decompressing resources or pre-building the main window.
    /// <para>用合成层 <c>Scale</c> 动画把填充平滑推向 <paramref name="ratio"/>，使阶段跳变表现为滑动而非突变。动画在合成线程
    /// 上运行，故 UI 线程解压资源或预构建主窗口时仍能持续推进。</para>
    /// </summary>
    /// <param name="ratio">Target fill ratio, clamped to <c>0..1</c>.<para>目标填充比例，被限制在 <c>0..1</c>。</para></param>
    private void ApplyProgress(double ratio)
    {
        ratio = Math.Clamp(ratio, 0d, 1d);
        if (!_fillComposed)
        {
            _pendingRatio = ratio;
            return;
        }

        if (ElementComposition.GetElementVisual(ProgressFill) is not { } visual)
        {
            _pendingRatio = ratio;
            return;
        }

        if (!_fillRevealed)
        {
            ProgressFill.Opacity = 1d;
            _fillRevealed = true;
        }

        var from = (float)_currentRatio;
        var to = (float)ratio;
        _currentRatio = ratio;

        var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(450);
        animation.InsertKeyFrame(0f, new Vector3D(from, 1f, 1f));
        animation.InsertKeyFrame(1f, new Vector3D(to, 1f, 1f), new CubicEaseOut());
        visual.StartAnimation("Scale", animation);
        // Commit the end value as the base so the fill settles there when the transient animation completes.
        visual.Scale = new Vector3D(to, 1f, 1f);
    }

    /// <summary>
    /// Allows closing only after the startup sequence has completed.
    /// <para>等待启动序列完成后允许关闭。</para>
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
    /// Creates and returns the main window (called by the Ursa SplashWindow on close);
    /// prefers the instance pre-built during the startup sequence to avoid transition jank.
    /// <para>创建并返回主窗口（由 Ursa SplashWindow 在关闭时调用）；优先返回启动序列中
    /// 预构建的实例，避免过渡到主窗体时卡顿。</para>
    /// </summary>
    protected override Task<Window?> CreateNextWindow()
        => Task.FromResult<Window?>(PreparedMainWindow ?? new MainWindow());
}