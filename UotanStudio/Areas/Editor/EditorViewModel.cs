using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Core.Editor;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.Editor;

/// <summary>
/// <para>多文档编辑器视图模型：管理一组打开的文本文档（Documents），记录脏标记，
/// 提供打开/关闭/保存动作。</para>
/// Multi-document editor view model: manages a set of open text documents,
/// tracks dirty state, and offers open/close/save actions.
/// </summary>
public partial class EditorViewModel : ViewModelBase
{
    /// <summary>
    /// <para>获取共享的编辑器视图模型单例，供工作区停靠布局与窗口菜单共用同一文档集合。</para>
    /// Gets the shared editor view model singleton, used by the workspace dock and the
    /// window menu to share the same document collection.
    /// </summary>
    public static EditorViewModel Instance { get; } = new();

    /// <summary>
    /// <para>获取已打开的文档集合。</para>
    /// Gets the collection of open documents.
    /// </summary>
    public ObservableCollection<OpenDocument> Documents { get; } = [];

    /// <summary>
    /// <para>初始化编辑器视图模型并订阅文档打开消息。</para>
    /// Initializes the editor view model and subscribes to document-open messages.
    /// </summary>
    public EditorViewModel()
    {
        WeakReferenceMessenger.Default.Register<OpenDocumentMessage>(this, OnOpenDocumentMessage);
    }

    /// <summary>
    /// <para>获取或设置当前选中文档。</para>
    /// Gets or sets the currently selected document.
    /// </summary>
    public OpenDocument? SelectedDocument
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>处理文档打开消息：调用现有 <see cref="OpenFileAsync"/> 打开指定文件。</para>
    /// Handles the document-open message: opens the requested file via the existing
    /// <see cref="OpenFileAsync"/> method.
    /// </summary>
    /// <param name="recipient"><para>消息接收者。</para>The message recipient.</param>
    /// <param name="message"><para>文档打开消息。</para>The document-open message.</param>
    private async void OnOpenDocumentMessage(object recipient, OpenDocumentMessage message)
    {
        await OpenFileAsync(message.Path);
    }

    /// <summary>
    /// <para>打开一个文本文件为一个文档；已打开时切换到它。</para>
    /// Opens a text file as a document; switches to it if already open.
    /// </summary>
    /// <param name="path"><para>文件路径。</para>File path.</param>
    /// <returns>打开是否成功。</returns>
    public async Task<bool> OpenFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var doc in Documents)
        {
            if (string.Equals(doc.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                SelectedDocument = doc;
                return true;
            }
        }

        try
        {
            var kind = DocumentKindDetector.Detect(path);
            OpenDocument doc;
            if (kind == EditorDocumentKind.Hex)
            {
                var bytes = await File.ReadAllBytesAsync(path);
                doc = new OpenDocument(path, string.Empty) { Kind = kind, BinaryData = bytes };
            }
            else
            {
                var text = await File.ReadAllTextAsync(path);
                doc = new OpenDocument(path, text) { Kind = kind };
            }

            Documents.Add(doc);
            SelectedDocument = doc;
            LogService.Instance.Write(LogLevel.Info, "Editor", $"Opened {path}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Editor", $"Failed to open {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <para>以给定内容创建一个文档（不绑定实际文件，用于占位/引导）。</para>
    /// Creates a document with the given content without a backing file (for placeholders).
    /// </summary>
    /// <param name="title"><para>文档标题文件名。</para>The document title (file name).</param>
    /// <param name="content"><para>文档内容。</para>The document content.</param>
    public void OpenFileFromText(string title, string content)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var doc = new OpenDocument(title, content);
        Documents.Add(doc);
        SelectedDocument = doc;
    }

    /// <summary>
    /// <para>保存指定文档到磁盘（若其未改动则直接返回）。</para>
    /// Saves the given document to disk (no-ops when it is not dirty).
    /// </summary>
    /// <param name="doc"><para>要保存的文档。</para>The document to save.</param>
    /// <returns>保存是否成功。</returns>
    private async Task<bool> SaveInternalAsync(OpenDocument doc)
    {
        if (doc is null || !doc.IsDirty)
        {
            return true;
        }

        try
        {
            await File.WriteAllTextAsync(doc.Path, doc.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            doc.MarkSaved();
            LogService.Instance.Write(LogLevel.Info, "Editor", $"Saved {doc.Path}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Editor", $"Failed to save {doc.Path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <para>保存当前选中文档。</para>
    /// Saves the currently selected document.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedDocument is not null)
        {
            await SaveInternalAsync(SelectedDocument);
        }
    }

    /// <summary>
    /// <para>关闭指定文档；若其有未保存改动则先保存。</para>
    /// Closes the given document; saves unsaved changes first.
    /// </summary>
    /// <param name="doc"><para>要关闭的文档。</para>The document to close.</param>
    public async void CloseDocument(OpenDocument doc)
    {
        if (doc is null)
        {
            return;
        }

        if (doc.IsDirty && doc.HasBackingFile)
        {
            var tcs = new TaskCompletionSource<bool>();
            CloseDocumentConfirmationRequested?.Invoke(this, (doc, tcs));
            var confirmed = await tcs.Task;
            if (!confirmed)
            {
                return;
            }
            await SaveInternalAsync(doc);
        }

        Documents.Remove(doc);
        if (ReferenceEquals(SelectedDocument, doc))
        {
            SelectedDocument = Documents.Count > 0 ? Documents[0] : null;
        }
    }

    /// <summary>
    /// <para>当需要确认关闭未保存文档时引发，由视图处理对话框并将结果写入 TaskCompletionSource。</para>
    /// Raised when closing an unsaved document needs confirmation; the view handles the
    /// dialog and writes the result to the TaskCompletionSource.
    /// </summary>
    public event EventHandler<(OpenDocument Document, TaskCompletionSource<bool> Confirmation)>? CloseDocumentConfirmationRequested;
}

/// <summary>
/// <para>编辑器中的单个打开的文档模型，含文本内容与脏标记。</para>
/// A single opened document in the editor, carrying text content and dirty state.
/// </summary>
/// <remarks>
/// <para>初始化文档并加载内容。</para>
/// Initializes the document and loads its content.
/// </remarks>
/// <param name="path"><para>文件绝对路径。</para>Absolute file path.</param>
/// <param name="content"><para>初始内容。</para>Initial content.</param>
public partial class OpenDocument(string path, string content) : ViewModelBase
{

    /// <summary>文件绝对路径。 Absolute file path.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// <para>获取文档的编辑器类型，决定展示视图（文本/二进制/结构化）。</para>
    /// Gets the editor document kind, selecting the display view (text/binary/structured).
    /// </summary>
    public EditorDocumentKind Kind { get; init; } = EditorDocumentKind.Text;

    /// <summary>
    /// <para>获取二进制文档的原始数据；仅当 <see cref="Kind"/> 为 Hex 时非空。</para>
    /// Gets the raw binary data of a binary document; non-null only when <see cref="Kind"/> is Hex.
    /// </summary>
    public byte[]? BinaryData { get; init; }

    /// <summary>显示标题（文件名）。 Display title (file name).</summary>
    public string Title => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// <para>获取值，指示文档是否有真实文件系统中的后备文件（非占位文档）。</para>
    /// Gets a value indicating whether the document has a real backing file in the file system
    /// (not a placeholder document).
    /// </summary>
    public bool HasBackingFile => System.IO.Path.IsPathRooted(Path) && System.IO.File.Exists(Path);

    /// <summary>
    /// <para>文本内容后备字段。</para>
    /// Text content backing field.
    /// </summary>
    [ObservableProperty]
    private string _text = content;

    partial void OnTextChanged(string value) => IsDirty = true;

    /// <summary>
    /// <para>是否包含未保存改动。</para>
    /// Whether the document contains unsaved changes.
    /// </summary>
    [ObservableProperty]
    private bool _isDirty;

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DisplayTitle));

    /// <summary>
    /// <para>显示标题（带脏标记星号）。</para>
    /// Display title with a dirty-marker asterisk.
    /// </summary>
    public string DisplayTitle => IsDirty ? Title + " *" : Title;

    /// <summary>
    /// <para>标记文档已保存并刷新标题。</para>
    /// Marks the document as saved and refreshes its title.
    /// </summary>
    public void MarkSaved() => IsDirty = false;
}