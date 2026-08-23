using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;

namespace UotanStudio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // 在 InitializeComponent 之前设置 DataContext，使子控件绑定尽早可用；
        // 工作区 DockControl 的布局由 WorkspaceView 自行在模板实例化前提供（见其构造函数）。
        // Set the DataContext before InitializeComponent so child bindings resolve early;
        // the workspace DockControl's layout is provided by WorkspaceView itself before
        // its template instantiates (see its constructor).
        DataContext = new ViewModels.MainViewModel();
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

    /// <summary>导航到设备管理标签页（索引 0）。</summary>
    private void OnDeviceManagerClick(object? sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new NavigateTabMessage(0));

    /// <summary>导航到设置标签页（索引 4）。</summary>
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new NavigateTabMessage(4));

    /// <summary>重新加载所有语言包。</summary>
    private void OnReloadLanguagePacksClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            LanguageService.Instance.Initialize();
        }
        catch
        {
            // 忽略重载过程中的错误
        }
    }

    /// <summary>清空节点编辑器画布（导航到节点标签页并发送清空消息）。</summary>
    private void OnClearCanvasClick(object? sender, RoutedEventArgs e)
    {
        // 导航到节点编辑器标签页（索引 2），由 MainView 中的接收器触发 ClearAllCommand
        WeakReferenceMessenger.Default.Send(new NavigateTabMessage(2));
        WeakReferenceMessenger.Default.Send(new ClearCanvasMessage());
    }

    /// <summary>退出应用程序。</summary>
    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>新建项目（当前为桩，弹出提示）。</summary>
    private async void OnNewClick(object? sender, RoutedEventArgs e) => await ShowInfoDialog("New Project", "New project creation is not yet implemented.");

    /// <summary>打开项目（当前为桩，弹出提示）。</summary>
    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await ShowInfoDialog("Open Project", "Open project is not yet implemented.");

    /// <summary>保存项目（当前为桩，弹出提示）。</summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await ShowInfoDialog("Save Project", "Save project is not yet implemented.");

    /// <summary>显示关于对话框。</summary>
    private async void OnAboutClick(object? sender, RoutedEventArgs e) => await ShowAboutDialog();

    private async System.Threading.Tasks.Task ShowAboutDialog() => await ShowInfoDialog("About UotanStudio", "UotanStudio\nVersion: 1.0.0\n\nA cross-platform Android device toolbox.");

    private async System.Threading.Tasks.Task ShowInfoDialog(string title, string text)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Text = text,
                Margin = new Thickness(20),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        await dialog.ShowDialog(this);
    }
}