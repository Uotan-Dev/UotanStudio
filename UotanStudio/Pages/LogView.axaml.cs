using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

/// <summary>
/// Log page view, providing real-Time log viewing, filtering, pause, and export interface.
/// <para>日志页面视图，提供实时日志查看、过滤、暂停与导出界面。</para>
/// </summary>
public partial class LogView : UserControl
{
    private LogViewModel? _subscribedVm;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogView"/> class.
    /// <para>初始化 <see cref="LogView"/> 类的新实例。</para>
    /// </summary>
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Handles the DataContextChanged event: re-subscribes to the filtered collection so
    /// auto-scroll can follow the latest entry.
    /// <para>处理 DataContextChanged 事件：重新订阅过滤集合，使自动滚动能跟随最新条目。</para>
    /// </summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        _subscribedVm?.FilteredEntries.CollectionChanged -= OnEntriesChanged;

        _subscribedVm = DataContext as LogViewModel;
        _subscribedVm?.FilteredEntries.CollectionChanged += OnEntriesChanged;
    }

    /// <summary>
    /// Scrolls the list to the newest entry when auto-scroll is enabled.
    /// <para>自动滚动开启时将列表滚动到最新条目。</para>
    /// </summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_subscribedVm is not { AutoScroll: true } || _subscribedVm.FilteredEntries.Count == 0)
        {
            return;
        }

        LogList.ScrollIntoView(_subscribedVm.FilteredEntries[^1]);
    }

    /// <summary>
    /// Pops a save-file dialog and exports logs to the user-selected path.
    /// <para>弹出保存文件对话框，将日志导出到用户选择的路径。</para>
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Logs",
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Text Files")
                {
                    Patterns = ["*.txt"]
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        if (file == null)
        {
            return;
        }

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await vm.ExportLogsAsync(path);
    }
}