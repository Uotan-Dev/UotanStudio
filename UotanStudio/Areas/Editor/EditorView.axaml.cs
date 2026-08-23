using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace UotanStudio.Areas.Editor;

/// <summary>
/// Editor view: hosts open text documents in tabs with a text editor.
/// <para>编辑器视图：以标签页承载打开的文本文档并提供一个文本编辑器。</para>
/// </summary>
public partial class EditorView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EditorView"/> class.
    /// <para>初始化 <see cref="EditorView"/> 类的新实例。</para>
    /// </summary>
    public EditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Handles the open button: picks a text file and opens it in a document.
    /// <para>处理打开按钮：选择文本文件并在文档中打开。</para>
    /// </summary>
    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Text File",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Text/Config Files")
                {
                    Patterns = ["*.txt", "*.prop", "*.rc", "*.conf", "*.xml", "*.json", "*.dts", "*.uotproj", "*.*"]
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                await vm.OpenFileAsync(path);
            }
        }
    }

    /// <summary>
    /// Selects the tapped document tab.
    /// <para>选中被点击的文档标签页。</para>
    /// </summary>
    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm || sender is not Control control)
        {
            return;
        }

        var doc = control.DataContext as OpenDocument;
        if (doc is not null && vm.Documents.Contains(doc))
        {
            vm.SelectedDocument = doc;
        }
    }

    /// <summary>
    /// Closes the document whose tab was clicked.
    /// <para>关闭被点击标签页对应的文档。</para>
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm)
        {
            return;
        }

        if (sender is not Button { Tag: OpenDocument doc })
        {
            return;
        }

        vm.CloseDocument(doc);
        e.Handled = true;
    }
}