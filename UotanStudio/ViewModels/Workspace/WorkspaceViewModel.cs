using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Controls;
using UotanStudio.Views.Workspace;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// Workspace view model, hosting the dock layout and managing opening and focusing documents in the
/// document area.
/// <para>工作区视图模型，承载 Dock 布局，并管理文档区文档的打开与聚焦。</para>
/// </summary>
public class WorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly IFactory _factory;
    private DocumentDock? _documentDock;
    private bool _subscribedDocumentDock;
    private CompareViewModel? _compareDocument;
    private ScreenMirrorViewModel? _screenMirrorDocument;
    internal ProjectExplorerViewModel? ProjectExplorer => (_factory as WorkspaceDockFactory)?.ProjectExplorer;
    /// <summary>
    /// Exposes the concrete workspace factory for project-lifecycle document cleanup.
    /// <para>供工程生命周期清理文档使用。</para>
    /// </summary>
    internal WorkspaceDockFactory? DockFactory => _factory as WorkspaceDockFactory;

    /// <summary>
    /// Gets or sets the dock root layout.
    /// <para>获取或设置 Dock 根布局。</para>
    /// </summary>
    public IRootDock? Layout
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Constructs the workspace view model: creates the dock factory and layout, then initializes the layout.
    /// <para>构造工作区视图模型：创建 Dock 工厂与布局，并完成布局初始化。</para>
    /// </summary>
    public WorkspaceViewModel() : this(new WorkspaceDockFactory()) { }

    internal WorkspaceViewModel(IFactory factory)
    {
        _factory = factory;
        Layout = _factory.CreateLayout();
        if (Layout != null)
        {
            _factory.InitLayout(Layout);
        }
    }

    /// <summary>
    /// Opens or focuses the compare document, reusing its instance until closed, and navigates to the workspace.
    /// <para>在工作区文档区打开或聚焦对比文档，复用尚未关闭的实例并切换到工作区。</para>
    /// </summary>
    /// <returns>The compare document, or null if the layout is unavailable. <para>对比文档；布局未就绪时为 null。</para></returns>
    public CompareViewModel? OpenCompareDocument()
    {
        if (_compareDocument?.Owner is not IDock owner || owner.VisibleDockables?.Contains(_compareDocument) != true)
        {
            var documentDock = _documentDock ??= FindDocumentsPane();
            if (documentDock is null)
            {
                return null;
            }

            EnsureDocumentDockSubscribed(documentDock);
            _compareDocument = new CompareViewModel
            {
                Id = "CompareDocument",
                Title = LanguageService.Instance.GetString("Tab_Compare"),
                CanClose = true
            };
            _factory.AddDockable(documentDock, _compareDocument);
        }

        _factory.SetActiveDockable(_compareDocument);
        if (_factory.FindRoot(_compareDocument) is { } root)
        {
            _factory.SetFocusedDockable(root, _compareDocument);
        }
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
        return _compareDocument;
    }

    /// <summary>
    /// Opens a new compare document in the workspace document area instead of reusing an existing one.
    /// <para>在工作区文档区新建一个对比文档，不复用已有实例。</para>
    /// </summary>
    /// <returns>The new compare document, or null if the layout is unavailable. <para>新建的对比文档；布局未就绪时为 null。</para></returns>
    public CompareViewModel? OpenNewCompareDocument()
    {
        var documentDock = _documentDock ??= FindDocumentsPane();
        if (documentDock is null)
        {
            return null;
        }

        EnsureDocumentDockSubscribed(documentDock);
        var document = new CompareViewModel
        {
            Id = $"CompareDocument{Guid.NewGuid():N}",
            Title = LanguageService.Instance.GetString("Tab_Compare"),
            CanClose = true
        };
        _factory.AddDockable(documentDock, document);
        _factory.SetActiveDockable(document);
        if (_factory.FindRoot(document) is { } root)
        {
            _factory.SetFocusedDockable(root, document);
        }

        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
        return document;
    }

    /// <summary>
    /// Opens or focuses the screen-mirror document, reusing its instance until closed,
    /// and navigates to the workspace.
    /// <para>在工作区文档区打开或聚焦投屏文档，复用尚未关闭的实例并切换到工作区。</para>
    /// </summary>
    /// <returns>The screen-mirror document, or null if the layout is unavailable. <para>投屏文档；布局未就绪时为 null。</para></returns>
    public ScreenMirrorViewModel? OpenScreenMirrorDocument()
    {
        if (_screenMirrorDocument?.Owner is not IDock owner || owner.VisibleDockables?.Contains(_screenMirrorDocument) != true)
        {
            var documentDock = _documentDock ??= FindDocumentsPane();
            if (documentDock is null)
            {
                return null;
            }

            EnsureDocumentDockSubscribed(documentDock);
            _screenMirrorDocument = new ScreenMirrorViewModel
            {
                Id = "ScreenMirrorDocument",
                Title = LanguageService.Instance.GetString("Tab_ScreenMirror"),
                CanClose = true
            };
            _factory.AddDockable(documentDock, _screenMirrorDocument);
        }

        _factory.SetActiveDockable(_screenMirrorDocument);
        if (_factory.FindRoot(_screenMirrorDocument) is { } root)
        {
            _factory.SetFocusedDockable(root, _screenMirrorDocument);
        }
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
        return _screenMirrorDocument;
    }

    /// <summary>
    /// Opens a document from the unified request, delegating to the path+mode overload.
    /// <para>按统一文档打开请求打开文档，转调路径+模式重载。</para>
    /// </summary>
    /// <param name="request">The unified open request. <para>统一打开请求。</para></param>
    internal Task OpenDocumentAsync(Services.Workspace.Documents.DocumentOpenRequest request)
        => OpenDocumentAsync(request.Path ?? string.Empty, request.Mode);

    /// <summary>
    /// Asynchronously opens the document at the given path: focuses it if the path is already open in
    /// the document area; otherwise creates a <see cref="FileViewModel"/>, resolves the syntax language
    /// by extension, adds it to the document area and activates it.
    /// <para>异步打开指定路径的文档：若该路径已在文档区打开则聚焦并返回；否则创建
    /// <see cref="FileViewModel"/>，按扩展名解析语法语言，加入文档区并激活。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the document file to open.
    /// <para>要打开的文档文件路径。</para>
    /// </param>
    /// <param name="mode">
    /// The requested open mode; default <c>Auto</c> decided by file type+extension.
    /// <para>请求的打开方式；默认 <c>Auto</c> 由文件类型+后缀综合判定。</para>
    /// </param>
    public async Task OpenDocumentAsync(string path, EditorOpenMode mode = EditorOpenMode.Auto)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FormatDetectionResult? detection = null;
        if (mode == EditorOpenMode.Auto)
        {

            byte[]? header = null;
            try
            {
                header = FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            }
            catch
            {

            }

            FirmwareFormatRegistry.RegisterAll();
            if (header is null || !header.AsSpan().StartsWith("\u007fELF"u8))
            {
                detection = await Task.Run(() => FormatDetector.DetectDetailed(path)).ConfigureAwait(true);
            }
            mode = Services.Workspace.Documents.DocumentOpenRegistry.ResolveMode(path, header, detection);
        }

        var descriptor = Services.Workspace.Documents.DocumentOpenRegistry.ByMode(mode);
        if (descriptor?.Open is { } open)
        {
            await open(this, new Services.Workspace.Documents.DocumentOpenRequest
            {
                Path = path,
                Mode = mode,
                Detection = detection
            });
            return;
        }

        await OpenTextAsync(path, mode);
    }

    /// <summary>
    /// Opens or focuses a document through the text fallback path: switches mode and reloads when the
    /// path is already open, otherwise creates a <see cref="FileViewModel"/>, adds it to the document
    /// area and activates it. Invoked when automatic resolution matches no editor descriptor.
    /// <para>以文本兜底路径打开或聚焦文档：路径已打开则按需切换模式并载入，否则新建
    /// <see cref="FileViewModel"/> 加入文档区并激活。供自动解析未命中编辑器描述符时调用。</para>
    /// </summary>
    /// <param name="path">The document file path. <para>文档文件路径。</para></param>
    /// <param name="mode">The resolved open mode. <para>解析后的打开方式。</para></param>
    internal async Task OpenTextAsync(string path, EditorOpenMode mode)
    {
        var documentDock = _documentDock ??= FindDocumentsPane();
        if (documentDock is null)
        {
            return;
        }

        EnsureDocumentDockSubscribed(documentDock);

        var fullPath = Path.GetFullPath(path);
        var existing = documentDock.VisibleDockables?
            .OfType<FileViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {

            if (mode != EditorOpenMode.Auto && existing.OpenMode != mode)
            {
                existing.OpenMode = mode;
                await existing.LoadContentAsync(fullPath, mode);
            }

            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new FileViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath,
            Language = SyntaxLanguageRegistry.GetLanguageId(fullPath) ?? string.Empty,
            OpenMode = mode
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        await document.LoadContentAsync(fullPath, mode);

        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            Services.Workspace.DocumentNotifications.Publish(document);
        }
    }

    /// <summary>
    /// Opens or focuses an offline log document.
    /// <para>打开或聚焦离线日志文档。</para>
    /// </summary>
    public async Task OpenLogAsync(string path)
    {
        var documentDock = _documentDock ??= FindDocumentsPane();
        if (documentDock is null || string.IsNullOrWhiteSpace(path)) return;
        EnsureDocumentDockSubscribed(documentDock);
        var fullPath = Path.GetFullPath(path);
        var existing = documentDock.VisibleDockables?.OfType<LogEditorViewModel>()
            .FirstOrDefault(document => string.Equals(document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }
        var document = new LogEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath };
        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;
        await document.LoadAsync(fullPath);
    }

    /// <summary>
    /// Opens the given device-tree blob in the DTB dual-pane editor in the document area: focuses it if
    /// already open; otherwise creates and loads a <see cref="DtbEditorViewModel"/>.
    /// <para>在文档区以 DTB 双栏编辑器打开指定的设备树二进制文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="DtbEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the DTB file to open.
    /// <para>要打开的 DTB 文件路径。</para>
    /// </param>
    public Task OpenDtbAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new DtbEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// Opens the given DEX file in the DEX dual-pane editor in the document area: focuses it if
    /// already open; otherwise creates and loads a <see cref="DexEditorViewModel"/>.
    /// <para>在文档区以 DEX 双栏编辑器打开指定的 DEX 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="DexEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the DEX file to open.
    /// <para>要打开的 DEX 文件路径。</para>
    /// </param>
    public Task OpenDexAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new DexEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>Opens or activates an ELF information document for the supplied file.</summary>
    public Task OpenElfAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ElfInfoViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>Opens or activates Qualcomm static image analysis for a local file.
    /// <para>为本地文件打开或激活 Qualcomm 镜像静态分析。</para></summary>
    /// <param name="path">Local image path. <para>本地镜像路径。</para></param>
    /// <returns>Completes when analysis has loaded. <para>分析加载完成后结束。</para></returns>
    public Task OpenQualcommAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new QualcommImageViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// Opens the given QCN file in the structured QCN editor in the document area: focuses it if
    /// already open; otherwise creates and loads a <see cref="QcnEditorViewModel"/>.
    /// <para>在文档区以结构化 QCN 编辑器打开指定的 QCN 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="QcnEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the QCN file to open.
    /// <para>要打开的 QCN 文件路径。</para>
    /// </param>
    public Task OpenQcnAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new QcnEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// Opens the given AXML file in the Manifest editor in the document area: focuses it if already
    /// open; otherwise creates and loads a <see cref="ManifestEditorViewModel"/>.
    /// <para>在文档区以 Manifest 编辑器打开指定的 AXML 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="ManifestEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the AXML file to open.
    /// <para>要打开的 AXML 文件路径。</para>
    /// </param>
    public Task OpenManifestAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ManifestEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// Opens the given resources.arsc file in the ARSC resource-table editor in the document area:
    /// focuses it if already open; otherwise creates and loads an <see cref="ArscEditorViewModel"/>.
    /// <para>在文档区以 ARSC 资源表编辑器打开指定的 resources.arsc 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="ArscEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the ARSC file to open.
    /// <para>要打开的 ARSC 文件路径。</para>
    /// </param>
    public Task OpenArscAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ArscEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// Opens the given binary file in the Hex editor in the document area: focuses it if already
    /// open; otherwise creates and loads a <see cref="HexEditorViewModel"/>.
    /// <para>在文档区以 Hex 编辑器打开指定的二进制文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="HexEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the binary file to open.
    /// <para>要打开的二进制文件路径。</para>
    /// </param>
    public Task OpenHexAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new HexEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// Opens the given APK file in the APK metadata editor in the document area: focuses it if already
    /// open; otherwise creates and loads an <see cref="ApkEditorViewModel"/>.
    /// <para>在文档区以 APK 元数据编辑器打开指定的 APK 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="ApkEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the APK file to open.
    /// <para>要打开的 APK 文件路径。</para>
    /// </param>
    public Task OpenApkAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ApkEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// Opens the given firmware file in the firmware metadata panel in the document area: focuses it if
    /// already open; otherwise creates and loads a <see cref="FirmwareInfoViewModel"/>.
    /// <para>在文档区以固件元数据面板打开指定的固件文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="FirmwareInfoViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the firmware file to open.
    /// <para>要打开的固件文件路径。</para>
    /// </param>
    public async Task OpenFirmwareAsync(string path, FormatDetectionResult? detection = null)
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

        EnsureDocumentDockSubscribed(documentDock);

        var fullPath = Path.GetFullPath(path);
        var existing = documentDock.VisibleDockables?
            .OfType<FirmwareInfoViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new FirmwareInfoViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        try
        {
            await document.LoadAsync(fullPath, detection);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Workspace",
                $"Failed to open firmware info for '{fullPath}': {ex.Message}");
        }

        if (document.HasError)
        {
            LogService.Instance.Write(LogLevel.Warn, "Workspace",
                $"Firmware info could not parse '{fullPath}'; falling back to the hex editor.");
            documentDock.VisibleDockables?.Remove(document);
            if (ReferenceEquals(documentDock.ActiveDockable, document))
            {
                documentDock.ActiveDockable = documentDock.VisibleDockables?.FirstOrDefault();
            }

            await OpenHexAsync(path);
            return;
        }

        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            Services.Workspace.DocumentNotifications.Publish(document);
        }
    }

    /// <summary>
    /// Opens the given filesystem image (ext4/EROFS/F2FS raw img) in the file-manager document editor in
    /// the document area: focuses it if already open; otherwise creates and loads a
    /// <see cref="FileManagerEditorViewModel"/>.
    /// <para>在文档区以文件管理器文档编辑器打开指定的文件系统镜像（ext4/EROFS/F2FS raw img）：
    /// 若已打开则聚焦并返回；否则创建并加载 <see cref="FileManagerEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The path of the image file to open.
    /// <para>要打开的镜像文件路径。</para>
    /// </param>
    public async Task OpenFileSystemAsync(string path)
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

        EnsureDocumentDockSubscribed(documentDock);

        var fullPath = Path.GetFullPath(path);
        var existing = documentDock.VisibleDockables?
            .OfType<FileManagerEditorViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new FileManagerEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        try
        {
            await document.LoadFromImageAsync(fullPath);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Workspace",
                $"Failed to open filesystem image '{fullPath}': {ex.Message}");
        }

        Services.Workspace.DocumentNotifications.Publish(document);
    }

    /// <summary>Opens a typed document and refreshes its properties after loading only while it remains active.</summary>
    internal async Task OpenOrActivateDocumentAsync<TDocument>(string path,
        Func<TDocument, string?> getPath, Func<string, TDocument> create,
        Func<TDocument, string, Task> load) where TDocument : class, IDockable
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var documentDock = _documentDock ??= FindDocumentsPane();
        if (documentDock is null) return;
        EnsureDocumentDockSubscribed(documentDock);
        var fullPath = Path.GetFullPath(path);
        var existing = documentDock.VisibleDockables?.OfType<TDocument>()
            .FirstOrDefault(document => string.Equals(getPath(document), fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }
        var document = create(fullPath);
        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;
        await load(document, fullPath);
        if (ReferenceEquals(documentDock.ActiveDockable, document))
            Services.Workspace.DocumentNotifications.Publish(document);
    }

    /// <summary>
    /// Subscribes to the document dock's <see cref="IDock.ActiveDockable"/> changes, sending an
    /// <see cref="ActiveDocumentChangedMessage"/> when the user switches between already-open document
    /// tabs so subscribers such as the properties panel can refresh.
    /// <para>订阅文档区 <see cref="IDock.ActiveDockable"/> 变更，在用户切换已打开文档标签时
    /// 发送 <see cref="ActiveDocumentChangedMessage"/>，使属性面板等订阅方刷新显示。</para>
    /// </summary>
    private void EnsureDocumentDockSubscribed(DocumentDock documentDock)
    {
        if (_subscribedDocumentDock)
        {
            return;
        }

        documentDock.PropertyChanged += OnDocumentDockPropertyChanged;
        _subscribedDocumentDock = true;
    }

    private void OnDocumentDockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IDock.ActiveDockable) || sender is not DocumentDock dock)
        {
            return;
        }

        Services.Workspace.DocumentNotifications.Publish(dock.ActiveDockable);
    }

    /// <summary>
    /// Finds and caches the document area (DocumentsPane) by Id in the layout; returns <c>null</c> when
    /// the layout is not ready.
    /// <para>在布局中按 Id 查找文档区（DocumentsPane）并缓存其引用；布局未就绪时返回 <c>null</c>。</para>
    /// </summary>
    public DocumentDock? FindDocumentsPanePublic() => _documentDock ??= FindDocumentsPane();

    private DocumentDock? FindDocumentsPane()
    {
        if (Layout is not null && _factory.FindDockable(Layout, dockable => dockable.Id == "DocumentsPane") is DocumentDock documentDock)
        {
            return documentDock;
        }

        return null;
    }

    /// <summary>
    /// Restores a document tab recorded in the persisted snapshot. Dispatches by
    /// <see cref="OpenDocumentDto.Kind"/> to the appropriate open method.
    /// <para>从持久化快照恢复一个已打开的文档标签。根据 <see cref="OpenDocumentDto.Kind"/> 分派到对应的打开方法。</para>
    /// </summary>
    /// <param name="dto">The document descriptor. <para>文档描述符。</para></param>
    /// <param name="rootDir">The project root directory. <para>工程根目录（用于拼接绝对路径）。</para></param>
    public async Task RestoreOpenDocumentAsync(Services.Projects.OpenDocumentDto dto, string rootDir)
    {
        if (dto.Kind == "Compare")
        {

            var compare = _compareDocument is null ? OpenCompareDocument() : OpenNewCompareDocument();
            if (compare is not null)
                foreach (var path in new[] { dto.CompareLeftPath, dto.CompareRightPath })
                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                        await compare.AddComparisonPathAsync(path);
            return;
        }
        var fullPath = Path.Combine(rootDir, dto.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return;
        }

        var descriptor = Services.Workspace.Documents.DocumentOpenRegistry.ByKind(dto.Kind);
        if (descriptor?.Open is not { } open)
        {
            return;
        }

        var mode = Enum.IsDefined((EditorOpenMode)dto.OpenMode)
            ? (EditorOpenMode)dto.OpenMode
            : EditorOpenMode.Auto;

        try
        {
            await open(this, new Services.Workspace.Documents.DocumentOpenRequest
            {
                Path = fullPath,
                Mode = mode
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Workspace",
                $"Failed to restore open document '{dto.RelativePath}' ({dto.Kind}): {ex.Message}");
        }
    }

    internal async Task RestoreDocumentsAsync(IEnumerable<Services.Projects.OpenDocumentDto> documents, string rootDir)
    {
        var snapshot = documents.ToList();
        foreach (var document in snapshot) await RestoreOpenDocumentAsync(document, rootDir);
        if (snapshot.LastOrDefault(d => d.IsActive) is { } active)
            await RestoreOpenDocumentAsync(active, rootDir);
    }

    internal async Task OpenPartitionAsync(string path)
    {
        var dock = FindDocumentsPanePublic();
        if (dock is null) return;
        EnsureDocumentDockSubscribed(dock);
        var document = dock.VisibleDockables?.OfType<PartitionEditorViewModel>().FirstOrDefault(d => d.FilePath == path);
        if (document is null)
        {
            document = new PartitionEditorViewModel { Title = Path.GetFileName(path) };
            _factory.AddDockable(dock, document);
            await document.LoadAsync(path);
        }
        dock.ActiveDockable = document;
    }

    /// <summary>
    /// Releases resources: unsubscribes from document-dock property-changed events.
    /// <para>释放资源：取消订阅文档区属性变更事件。</para>
    /// </summary>
    public void Dispose()
    {
        ProjectExplorer?.Dispose();
        if (_factory is WorkspaceDockFactory workspaceFactory) workspaceFactory.DisposeTerminal();
        if (_documentDock is not null)
        {
            _documentDock.PropertyChanged -= OnDocumentDockPropertyChanged;
        }

        _subscribedDocumentDock = false;
        GC.SuppressFinalize(this);
    }
}