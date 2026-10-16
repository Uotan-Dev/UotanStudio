using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// USB tree-view window: shows the system USB bus topology in the same hierarchical shape as the Windows
/// device manager, with backend selection, an Android filter, node details, text export and auto-refresh
/// on device changes.
/// <para>USB 树状视图窗口：以 Windows 设备管理器同款层级展示系统 USB 总线拓扑，并提供后端选择、
/// Android 过滤、节点详情、文本导出与设备变更自动刷新。</para>
/// </summary>
public partial class UsbTreeWindow : Window
{
    private readonly UsbTreeViewModel _model;

    /// <summary>
    /// Initializes the USB tree window with the host device-monitor service as its topology source.
    /// <para>初始化 USB 树窗口，使用宿主设备监视服务作为拓扑来源。</para>
    /// </summary>
    public UsbTreeWindow()
        : this(new UsbTreeViewModel())
    {
    }

    /// <summary>
    /// Initializes the USB tree window with an injected view model so tests can supply a fake topology
    /// source.
    /// <para>初始化 USB 树窗口并注入视图模型，供测试提供伪造的拓扑来源。</para>
    /// </summary>
    /// <param name="model">
    /// The view model the window consumes.
    /// <para>窗口消费的视图模型。</para>
    /// </param>
    internal UsbTreeWindow(UsbTreeViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = _model.RefreshAsync();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _model.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Writes the visible topology tree, with driver and interface diagnostics, to the clipboard; on
    /// failure the tree stays untouched and the status plus the unified log record the problem.
    /// <para>把当前可见拓扑树连同驱动与接口诊断写入剪贴板；失败时保留树内容并把状态写入统一日志。</para>
    /// </summary>
    private async void OnCopyTextClick(object? sender, RoutedEventArgs e)
    {
        var text = _model.BuildTextExport();
        if (text.Length == 0)
        {
            _model.StatusText = LanguageService.Instance.GetString("UsbTree_NothingToCopy");
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            _model.StatusText = LanguageService.Instance.GetString("UsbTree_CopyFailed");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            await clipboard.FlushAsync();
        }
        catch (Exception ex)
        {
            _model.StatusText = LanguageService.Instance.GetString("UsbTree_CopyFailed");
            LogService.Instance.Write(LogLevel.Error, "UsbTreeWindow",
                $"USB topology copy failed: {ex.Message}");
        }
    }
}