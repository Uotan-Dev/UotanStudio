using UotanStudio.ViewModels.Shell;
using Avalonia.Controls;
#if DEBUG
using Dock.Avalonia.Diagnostics;
#endif

namespace UotanStudio.Views.Shell;

public partial class MainWindow : Window
{
    private bool _closeAfterSave;
    private bool _closing;
    private Task _sessionInitialization = Task.CompletedTask;
#if DEBUG
    private IDisposable? _dockDebugOverlay;
#endif

    public MainWindow()
    {
        // 在 InitializeComponent 之前设置 DataContext，使子控件绑定尽早可用；
        // 也保证 SplashWindow 过渡的 fallback 路径（未预构建时）同样带数据上下文。
        DataContext = new MainViewModel();
        InitializeComponent();
        Closing += MainWindow_Closing;
        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel) _sessionInitialization = viewModel.InitializeAutoSaveAsync();
        };
#if DEBUG
        Opened += MainWindow_Opened;
#endif
    }

#if DEBUG
    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        _dockDebugOverlay?.Dispose();
        _dockDebugOverlay = this.AttachDockDebugOverlay();
    }
#endif

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeAfterSave && DataContext is MainViewModel viewModel)
        {
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            try
            {
                await _sessionInitialization;
                await viewModel.FlushAutoSaveAsync();
            }
            catch (Exception ex)
            {
                Services.LogService.Instance.Write(Services.LogLevel.Error, "AutoSave", ex.Message);
            }
            finally
            {
                _closeAfterSave = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(Close);
            }
            return;
        }
        try
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // 清理过程中的异常不阻断窗口关闭。
        }
    }
}
