using System;
using Avalonia.Controls;

namespace UotanStudio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
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