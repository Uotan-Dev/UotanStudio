using System;
using Avalonia.Controls;
using UotanStudio.ViewModels;

namespace UotanStudio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // 在 InitializeComponent 之前设置 DataContext，使子控件绑定尽早可用；
        // 也保证 SplashWindow 过渡的 fallback 路径（未预构建时）同样带数据上下文。
        DataContext = new MainViewModel();
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // 忽略清理过程中的任何错误，以防止应用程序崩溃
        }
    }
}