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
/// <para>工作区视图模型，承载 Dock 布局，并管理文档区文档的打开与聚焦。</para>
/// Workspace view model, hosting the dock layout and managing opening and focusing documents in the
/// document area.
/// </summary>
public class WorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly IFactory _factory;
    private IRootDock? _layout;
    private DocumentDock? _documentDock;
    private bool _subscribedDocumentDock;
    private CompareViewModel? _compareDocument;
    private ScreenMirrorViewModel? _screenMirrorDocument;
    internal ProjectExplorerViewModel? ProjectExplorer => (_factory as WorkspaceDockFactory)?.ProjectExplorer;
    /// <summary>Exposes the concrete workspace factory for project-lifecycle document cleanup. 供工程生命周期清理文档使用。</summary>
    internal WorkspaceDockFactory? DockFactory => _factory as WorkspaceDockFactory;

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
    /// <para>在工作区文档区打开或聚焦对比文档，复用尚未关闭的实例并切换到工作区。</para>
    /// Opens or focuses the compare document, reusing its instance until closed, and navigates to the workspace.
    /// </summary>
    /// <returns><para>对比文档；布局未就绪时为 null。</para>The compare document, or null if the layout is unavailable.</returns>
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
    /// <para>在工作区文档区新建一个对比文档（不复用已有实例），供「在新标签打开文件对比」使用；
    /// 新文档的 Id 唯一，避免与已打开的对比标签冲突。</para>
    /// Opens an additional compare document in the workspace document area (never reusing an existing instance),
    /// used by "open the file comparison in a new tab"; the new document gets a unique Id so it cannot clash with
    /// an already open compare tab.
    /// </summary>
    /// <returns><para>新建的对比文档；布局未就绪时为 null。</para>The new compare document, or null if the layout is unavailable.</returns>
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
    /// <para>在工作区文档区打开或聚焦投屏文档，复用尚未关闭的实例并切换到工作区。</para>
    /// Opens or focuses the screen-mirror document, reusing its instance until closed,
    /// and navigates to the workspace.
    /// </summary>
    /// <returns><para>投屏文档；布局未就绪时为 null。</para>The screen-mirror document, or null if the layout is unavailable.</returns>
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
    /// <param name="mode">
    /// <para>请求的打开方式；默认 <c>Auto</c> 由文件类型+后缀综合判定。</para>
    /// The requested open mode; default <c>Auto</c> decided by file type+extension.
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
                header = UotanStudio.Firmware.Core.FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            }
            catch
            {

            }

            FirmwareFormatRegistry.RegisterAll();
            if (header is null || !header.AsSpan().StartsWith("\u007fELF"u8))
            {
                detection = await Task.Run(() => FormatDetector.DetectDetailed(path)).ConfigureAwait(true);
            }
            mode = FileOpenMode.ResolveMode(path, header, detection);
        }

        if (mode == EditorOpenMode.Elf)
        {
            await OpenElfAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Qualcomm)
        {
            await OpenQualcommAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Qcn)
        {
            await OpenQcnAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Log)
        {
            await OpenLogAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Dtb)
        {
            await OpenDtbAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Dex)
        {
            await OpenDexAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Axml)
        {
            await OpenManifestAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Arsc)
        {
            await OpenArscAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Hex)
        {
            await OpenHexAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Apk)
        {
            await OpenApkAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Firmware)
        {
            await OpenFirmwareAsync(path, detection);
            return;
        }

        if (mode == EditorOpenMode.FileSystem)
        {
            await OpenFileSystemAsync(path);
            return;
        }

        if (mode == EditorOpenMode.Partition)
        {
            await OpenPartitionAsync(path);
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

    /// <summary>Opens or focuses an offline log document. 打开或聚焦离线日志文档。</summary>
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
    /// <para>在文档区以 DTB 双栏编辑器打开指定的设备树二进制文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="DtbEditorViewModel"/>。</para>
    /// Opens the given device-tree blob in the DTB dual-pane editor in the document area: focuses it if
    /// already open; otherwise creates and loads a <see cref="DtbEditorViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的 DTB 文件路径。</para>
    /// The path of the DTB file to open.
    /// </param>
    public Task OpenDtbAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new DtbEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// <para>在文档区以 DEX 双栏编辑器打开指定的 DEX 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="DexEditorViewModel"/>。</para>
    /// Opens the given DEX file in the DEX dual-pane editor in the document area: focuses it if
    /// already open; otherwise creates and loads a <see cref="DexEditorViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的 DEX 文件路径。</para>
    /// The path of the DEX file to open.
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
    /// <para>在文档区以结构化 QCN 编辑器打开指定的 QCN 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="QcnEditorViewModel"/>。</para>
    /// Opens the given QCN file in the structured QCN editor in the document area: focuses it if
    /// already open; otherwise creates and loads a <see cref="QcnEditorViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的 QCN 文件路径。</para>
    /// The path of the QCN file to open.
    /// </param>
    public Task OpenQcnAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new QcnEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// <para>在文档区以 Manifest 编辑器打开指定的 AXML 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="ManifestEditorViewModel"/>。</para>
    /// Opens the given AXML file in the Manifest editor in the document area: focuses it if already
    /// open; otherwise creates and loads a <see cref="ManifestEditorViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的 AXML 文件路径。</para>
    /// The path of the AXML file to open.
    /// </param>
    public Task OpenManifestAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ManifestEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// <para>在文档区以 ARSC 资源表编辑器打开指定的 resources.arsc 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="ArscEditorViewModel"/>。</para>
    /// Opens the given resources.arsc file in the ARSC resource-table editor in the document area:
    /// focuses it if already open; otherwise creates and loads an <see cref="ArscEditorViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的 ARSC 文件路径。</para>
    /// The path of the ARSC file to open.
    /// </param>
    public Task OpenArscAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ArscEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// <para>在文档区以 Hex 编辑器打开指定的二进制文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="HexEditorViewModel"/>。</para>
    /// Opens the given binary file in the Hex editor in the document area: focuses it if already
    /// open; otherwise creates and loads a <see cref="HexEditorViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的二进制文件路径。</para>
    /// The path of the binary file to open.
    /// </param>
    public Task OpenHexAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new HexEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// <para>在文档区以 APK 元数据编辑器打开指定的 APK 文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="ApkEditorViewModel"/>。</para>
    /// Opens the given APK file in the APK metadata editor in the document area: focuses it if already
    /// open; otherwise creates and loads an <see cref="ApkEditorViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的 APK 文件路径。</para>
    /// The path of the APK file to open.
    /// </param>
    public Task OpenApkAsync(string path) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ApkEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath));

    /// <summary>
    /// <para>在文档区以固件元数据面板打开指定的固件文件：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="FirmwareInfoViewModel"/>。</para>
    /// Opens the given firmware file in the firmware metadata panel in the document area: focuses it if
    /// already open; otherwise creates and loads a <see cref="FirmwareInfoViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的固件文件路径。</para>
    /// The path of the firmware file to open.
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
    /// <para>在文档区以文件管理器文档编辑器打开指定的文件系统镜像（ext4/EROFS/F2FS raw img）：
    /// 若已打开则聚焦并返回；否则创建并加载 <see cref="FileManagerEditorViewModel"/>。</para>
    /// Opens the given filesystem image (ext4/EROFS/F2FS raw img) in the file-manager document editor in
    /// the document area: focuses it if already open; otherwise creates and loads a
    /// <see cref="FileManagerEditorViewModel"/>.
    /// </summary>
    /// <param name="path">
    /// <para>要打开的镜像文件路径。</para>
    /// The path of the image file to open.
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
    /// <para>订阅文档区 <see cref="IDock.ActiveDockable"/> 变更，在用户切换已打开文档标签时
    /// 发送 <see cref="ActiveDocumentChangedMessage"/>，使属性面板等订阅方刷新显示。</para>
    /// Subscribes to the document dock's <see cref="IDock.ActiveDockable"/> changes, sending an
    /// <see cref="ActiveDocumentChangedMessage"/> when the user switches between already-open document
    /// tabs so subscribers such as the properties panel can refresh.
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
    /// <para>在布局中按 Id 查找文档区（DocumentsPane）并缓存其引用；布局未就绪时返回 <c>null</c>。</para>
    /// Finds and caches the document area (DocumentsPane) by Id in the layout; returns <c>null</c> when
    /// the layout is not ready.
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
    /// <para>从持久化快照恢复一个已打开的文档标签。根据 <see cref="OpenDocumentDto.Kind"/> 分派到对应的打开方法。</para>
    /// Restores a document tab recorded in the persisted snapshot. Dispatches by
    /// <see cref="OpenDocumentDto.Kind"/> to the appropriate open method.
    /// </summary>
    /// <param name="dto"><para>文档描述符。</para>The document descriptor.</param>
    /// <param name="rootDir"><para>工程根目录（用于拼接绝对路径）。</para>The project root directory.</param>
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

        try
        {
            switch (dto.Kind)
            {
                case "Text":
                    await OpenDocumentAsync(fullPath, Enum.IsDefined((EditorOpenMode)dto.OpenMode)
                        ? (EditorOpenMode)dto.OpenMode : EditorOpenMode.Auto);
                    break;
                case "Firmware":
                    await OpenFirmwareAsync(fullPath);
                    break;
                case "FileManager":
                    await OpenFileSystemAsync(fullPath);
                    break;
                case "Dtb":
                    await OpenDtbAsync(fullPath);
                    break;
                case "Dex":
                    await OpenDexAsync(fullPath);
                    break;
                case "Elf":
                    await OpenElfAsync(fullPath);
                    break;
                case "Qualcomm":
                    await OpenQualcommAsync(fullPath);
                    break;
                case "Manifest":
                    await OpenManifestAsync(fullPath);
                    break;
                case "Arsc":
                    await OpenArscAsync(fullPath);
                    break;
                case "Hex":
                    await OpenHexAsync(fullPath);
                    break;
                case "Apk":
                    await OpenApkAsync(fullPath);
                    break;
                case "Partition":
                    await OpenPartitionAsync(fullPath);
                    break;
            }
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

    private async Task OpenPartitionAsync(string path)
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
    /// <para>释放资源：取消订阅文档区属性变更事件。</para>
    /// Releases resources: unsubscribes from document-dock property-changed events.
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