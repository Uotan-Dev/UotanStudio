using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

/// <summary>
/// <para>日志页面视图，提供实时日志查看、过滤、暂停与导出界面。</para>
/// Log page view, providing real-time log viewing, filtering, pause, and export interface.
/// </summary>
public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // 当 DataContext 变化时，可在此处与外部设备状态关联
        // 当前实现：LogViewModel.SetActiveDevice 由外部调用方（如 MainViewModel）触发
    }

    /// <summary>
    /// <para>弹出保存文件对话框，将日志导出到用户选择的路径。</para>
    /// Pops a save-file dialog and exports logs to the user-selected path.
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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
