using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Standalone global log window with level-colored entries, filtering, clear, export, and selectable text.
/// <para>全局日志独立窗口，按等级着色显示日志并提供筛选、清空、导出与文本选择复制。</para>
/// </summary>
public partial class LogWindow : Window
{
    private static readonly LogViewModel SharedViewModel = new();
    private readonly LogTextPresenter<LogViewModel.LogEntryViewModel> _presenter;

    /// <summary>
    /// Initializes the log window with the shared view model that retains logs across window closure and reopening.
    /// <para>初始化日志窗口，并绑定到跨窗口关闭与重开保留日志的共享视图模型。</para>
    /// </summary>
    public LogWindow()
    {
        InitializeComponent();
        DataContext = SharedViewModel;
        LogEditor.Options.EnableHyperlinks = false;
        LogEditor.Options.EnableEmailHyperlinks = false;
        _presenter = new(this, LogEditor, static entry => entry.Text, static entry => entry.Level,
            () => SharedViewModel.AutoScroll, value => SharedViewModel.AutoScroll = value,
            value => CopySelectionMenuItem.IsEnabled = value);
        _presenter.Attach();
        _presenter.Rebuild(SharedViewModel.Entries);
        SharedViewModel.Entries.CollectionChanged += OnEntriesCollectionChanged;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        SharedViewModel.Entries.CollectionChanged -= OnEntriesCollectionChanged;
        _presenter.Detach();
        base.OnClosed(e);
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => _presenter.Update(e, SharedViewModel.Entries);

    /// <summary>
    /// Clear button click handler, clearing the current log collection and scrolling to bottom.
    /// <para>清空按钮点击事件，清空当前日志集合并滚动到底部。</para>
    /// </summary>
    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogViewModel vm)
        {
            vm.Clear();
        }
        _presenter.ScrollToEnd();
    }

    /// <summary>
    /// Export button click handler, opening a save dialog and writing the current log collection to a text file.
    /// <para>导出按钮点击事件，弹出保存对话框并将当前日志集合写入文本文件。</para>
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        await ExportLogsAsync();
    }

    /// <summary>
    /// Exports logs to a user-chosen file.
    /// <para>导出日志到用户选择的文件。</para>
    /// </summary>
    private async Task ExportLogsAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        if (DataContext is not LogViewModel vm || vm.Entries.Count == 0)
        {
            return;
        }

        var suggestedName = $"UotanStudio_Log_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Log_Export"),
            DefaultExtension = "log",
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            FileTypeChoices =
            [
                new FilePickerFileType("Log files")
                {
                    Patterns = ["*.log", "*.txt"],
                },
            ],
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            foreach (var entry in vm.Entries)
            {
                await writer.WriteLineAsync(
                    $"{entry.Timestamp} [{entry.Level}] {entry.Source} - {entry.Message}");
            }
            await writer.FlushAsync();

            LogService.Instance.Write(LogLevel.Info, "LogWindow",
                LanguageService.Instance.GetString("Log_ExportDone", file.Name));
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "LogWindow",
                LanguageService.Instance.GetString("Log_ExportFailed", ex.Message));
        }
    }

    private void OnCopySelectionClick(object? sender, RoutedEventArgs e) => LogEditor.Copy();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => LogEditor.SelectAll();

}