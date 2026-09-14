using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Boot 修补窗口的界面占位。后端修补流程尚未接入，文件选择仍可用于准备表单内容。
/// </summary>
public partial class BootPatchWindow : Window
{
    // Avalonia's runtime XAML loader requires a public parameterless constructor.
    public BootPatchWindow() : this(null)
    {
    }

    public BootPatchWindow(string? imagePath)
    {
        InitializeComponent();
        FileSourceBox.SelectionChanged += OnFileSourceChanged;
        BootPathBox.Text = ToAbsolutePath(imagePath);
        UpdateFileSourceState();
    }

    private static string? ToAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    private void OnFileSourceChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateFileSourceState();

    private void UpdateFileSourceState()
    {
        var customFile = FileSourceBox.SelectedIndex == 0;
        FilePathBox.IsEnabled = customFile;
        BrowseFileButton.IsEnabled = customFile;
    }

    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
        => await BrowseAsync(FilePathBox, "Boot image");

    private async void OnBrowseBootClick(object? sender, RoutedEventArgs e)
        => await BrowseAsync(BootPathBox, "Boot image");

    private async Task BrowseAsync(TextBox target, string description)
    {
        if (StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = description,
            FileTypeFilter = [new FilePickerFileType("Boot image") { Patterns = ["*.img", "*.bin"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            target.Text = path;
    }

    // 修补后端尚未接入；保留按钮和响应式布局，点击暂不启动耗时操作。
    private void OnStartPatchClick(object? sender, RoutedEventArgs e)
    {
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
