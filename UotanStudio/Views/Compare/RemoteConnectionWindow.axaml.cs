using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UotanStudio.ViewModels.Compare;

namespace UotanStudio.Views.Compare;

/// <summary>
/// Window for configuring and managing remote file connections (SFTP, SCP, FTP, WebDAV).
/// <para>用于配置和管理远程文件连接（SFTP、SCP、FTP、WebDAV）的窗口。</para>
/// </summary>
public partial class RemoteConnectionWindow : Window
{
    /// <summary>
    /// Gets the strongly typed view model of this window.
    /// <para>获取本窗口强类型的视图模型。</para>
    /// </summary>
    /// <value>The remote connection view model.<para>远程连接视图模型。</para></value>
    public RemoteConnectionViewModel ViewModel => (RemoteConnectionViewModel)DataContext!;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteConnectionWindow"/> class.
    /// <para>初始化 <see cref="RemoteConnectionWindow"/> 类的新实例。</para>
    /// </summary>
    public RemoteConnectionWindow()
    {
        InitializeComponent();
        var vm = new RemoteConnectionViewModel();
        DataContext = vm;
        vm.RequestClose += Close;
    }

    /// <summary>
    /// Handles the close button click event.
    /// <para>处理关闭按钮点击事件。</para>
    /// </summary>
    /// <param name="sender">The event sender.<para>事件源。</para></param>
    /// <param name="e">Event arguments.<para>事件参数。</para></param>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Handles double-tap on a history row to reconnect immediately.
    /// <para>处理历史记录行的双击事件以立即重连。</para>
    /// </summary>
    /// <param name="sender">The event sender.<para>事件源。</para></param>
    /// <param name="e">Event arguments.<para>事件参数。</para></param>
    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel.SelectedHistoryItem is { } item)
        {
            ViewModel.Reconnect(item);
        }
    }
}