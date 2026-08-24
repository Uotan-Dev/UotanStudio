using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using UotanStudio.Services;

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
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is EditorViewModel vm)
        {
            vm.CloseDocumentConfirmationRequested -= OnCloseDocumentConfirmationRequested;
            vm.CloseDocumentConfirmationRequested += OnCloseDocumentConfirmationRequested;
        }
    }

    /// <summary>
    /// 处理关闭未保存文档的确认请求，弹出 Yes/No/Cancel 对话框。
    /// <para>Handles the close-document confirmation request by showing a Yes/No/Cancel dialog.</para>
    /// </summary>
    private async void OnCloseDocumentConfirmationRequested(object? sender, (OpenDocument Document, TaskCompletionSource<bool> Confirmation) e)
    {
        var owner = this.FindAncestorOfType<Window>();
        if (owner is null)
        {
            e.Confirmation.TrySetResult(false);
            return;
        }

        var title = LanguageService.Instance.GetString("Editor_SaveConfirmTitle");
        var message = string.Format(LanguageService.Instance.GetString("Editor_SaveConfirmMessage"), e.Document.Title);
        var yes = LanguageService.Instance.GetString("Editor_Yes");
        var no = LanguageService.Instance.GetString("Editor_No");
        var cancel = LanguageService.Instance.GetString("Editor_Cancel");

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = yes, Tag = true },
                            new Button { Content = no, Tag = false },
                            new Button { Content = cancel, Tag = false }
                        }
                    }
                }
            }
        };

        var result = false;
        var completed = false;
        foreach (var btn in ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children)
        {
            if (btn is Button b)
            {
                b.Click += (_, _) =>
                {
                    if (b.Content?.ToString() == yes)
                    {
                        result = true;
                    }
                    else if (b.Content?.ToString() == no)
                    {
                        result = false;
                    }
                    else
                    {
                        // Cancel: do not close
                        e.Confirmation.TrySetResult(false);
                        completed = true;
                        dialog.Close();
                        return;
                    }
                    completed = true;
                    dialog.Close();
                };
            }
        }

        if (completed)
        {
            return;
        }

        await dialog.ShowDialog(owner);
        e.Confirmation.TrySetResult(result);
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