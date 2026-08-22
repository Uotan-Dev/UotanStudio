using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

/// <summary>
/// Log page view, providing real-time log viewing, filtering, pause, and export interface.
/// <para>日志页面视图，提供实时日志查看、过滤、暂停与导出界面。</para>
/// </summary>
public partial class LogView : UserControl
{
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
    /// Handles the DataContextChanged event.
    /// <para>处理 DataContextChanged 事件。</para>
    /// </summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
    }

    /// <summary>
    /// Pops a save-file dialog and exports logs to the user-selected path.
    /// <para>弹出保存文件对话框，将日志导出到用户选择的路径。</para>
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Logs",
            DefaultExtension = "txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text Files")
                {
                    Patterns = new[] { "*.txt" }
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (file == null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await vm.ExportLogsAsync(path);
    }
}
