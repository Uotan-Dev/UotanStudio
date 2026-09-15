using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>校验窗口：选择文件、按算法显示校验值并支持复制，以及「粘贴并校验」比对结论。</para>
/// Checksum window: file selection, per-algorithm result rows with copy support, and the "paste and verify"
/// comparison verdict.
/// </summary>
public partial class ChecksumWindow : Window
{
    /// <summary>
    /// <para>创建校验窗口（不带初始文件）。</para>
    /// Creates the checksum window without an initial file.
    /// </summary>
    public ChecksumWindow() : this(null)
    {
    }

    /// <summary>
    /// <para>创建校验窗口并预选文件。</para>
    /// Creates the checksum window with a pre-selected file.
    /// </summary>
    /// <param name="filePath"><para>预选文件路径。</para>The pre-selected file path.</param>
    public ChecksumWindow(string? filePath)
    {
        InitializeComponent();
        DataContext = new ChecksumViewModel();
        SetFile(filePath);
    }

    private ChecksumViewModel ViewModel => (ChecksumViewModel)DataContext!;

    /// <summary>
    /// <para>切换当前文件（资源管理器复用同一窗口时也会调用）；重选会清空下方结果与粘贴内容。</para>
    /// Switches the current file (also used when the explorer reuses this window); re-selecting clears the
    /// result rows and the pasted text.
    /// </summary>
    /// <param name="filePath"><para>文件路径；为空表示未选择文件。</para>The file path; empty means no file.</param>
    public void SetFile(string? filePath) => ViewModel.SetFilePath(filePath);

    private async void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                SetFile(path);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Checksum", $"Choose file failed: {ex.Message}");
        }
    }

    private void OnVerify(object? sender, RoutedEventArgs e) => ViewModel.Verify();

    private async void OnCopyMd5(object? sender, RoutedEventArgs e) => await CopyAsync(ViewModel.Md5Value);

    private async void OnCopySha1(object? sender, RoutedEventArgs e) => await CopyAsync(ViewModel.Sha1Value);

    private async void OnCopySha256(object? sender, RoutedEventArgs e) => await CopyAsync(ViewModel.Sha256Value);

    private async void OnCopyCrc(object? sender, RoutedEventArgs e) => await CopyAsync(ViewModel.CrcValue);

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard)
            {
                return;
            }

            if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            {
                ViewModel.PastedText = text;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Checksum", $"Paste failed: {ex.Message}");
        }
    }

    private async Task CopyAsync(string value)
    {
        try
        {
            if (value.Length == 0 || Clipboard is not { } clipboard)
            {
                return;
            }

            await clipboard.SetTextAsync(value);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Checksum", $"Copy failed: {ex.Message}");
        }
    }
}