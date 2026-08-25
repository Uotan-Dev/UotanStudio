using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Pages.DockControl;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>工作区视图模型，承载 Dock 布局，并管理文档区文档的打开与聚焦。</para>
/// Workspace view model, hosting the dock layout and managing opening and focusing documents in the
/// document area.
/// </summary>
public class WorkspaceViewModel : ViewModelBase
{
    private readonly IFactory _factory;
    private IRootDock? _layout;
    private DocumentDock? _documentDock;

    /// <summary>
    /// <para>获取或设置 Dock 根布局。</para>
    /// Gets or sets the dock root layout.
    /// </summary>
    public IRootDock? Layout
    {
        get => _layout;
        set => SetProperty(ref _layout, value);
    }

    /// <summary>
    /// <para>构造工作区视图模型：创建 Dock 工厂与布局，并完成布局初始化。</para>
    /// Constructs the workspace view model: creates the dock factory and layout, then initializes the layout.
    /// </summary>
    public WorkspaceViewModel()
    {
        _factory = new WorkspaceDockFactory();
        Layout = _factory.CreateLayout();
        if (Layout != null)
        {
            _factory.InitLayout(Layout);
        }
    }

    /// <summary>
    /// <para>异步打开指定路径的文档：若该路径已在文档区打开则聚焦并返回；否则创建
    /// <see cref="FileViewModel"/>，按扩展名解析语法语言，加入文档区并激活。</para>
    /// Asynchronously opens the document at the given path: focuses it if the path is already open in
    /// the document area; otherwise creates a <see cref="FileViewModel"/>, resolves the syntax language
    /// by extension, adds it to the document area and activates it.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的文档文件路径。</para>
    /// The path of the document file to open.
    /// </param>
    public async Task OpenDocumentAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var documentDock = _documentDock ??= FindDocumentsPane();
        if (documentDock is null)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var existing = documentDock.VisibleDockables?
            .OfType<FileViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new FileViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath,
            Language = SyntaxLanguageRegistry.GetLanguageId(fullPath) ?? string.Empty
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        await document.LoadContentAsync(fullPath);
    }

    /// <summary>
    /// <para>在布局中按 Id 查找文档区（DocumentsPane）并缓存其引用；布局未就绪时返回 <c>null</c>。</para>
    /// Finds and caches the document area (DocumentsPane) by Id in the layout; returns <c>null</c> when
    /// the layout is not ready.
    /// </summary>
    private DocumentDock? FindDocumentsPane()
    {
        if (Layout is not null && _factory.FindDockable(Layout, dockable => dockable.Id == "DocumentsPane") is DocumentDock documentDock)
        {
            return documentDock;
        }

        return null;
    }
}