using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
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

        // Auto 模式：按扩展名+内容综合解析，命中 DTB/DTBO 即走双栏编辑器，
        // 避免 .dtb 落入 FileViewModel 被当作普通二进制（十六进制）打开。
        // Auto mode: resolve by extension+content; DTB/DTBO blobs route to the
        // dual-pane editor instead of being opened as a plain binary (hex) document.
        if (mode == EditorOpenMode.Auto)
        {
            // 有界读取文件头参与格式与二进制内容探测：专用格式按魔数路由，其余明显二进制
            // 统一进入 HexEditorView，避免 FileView 内嵌 Hex 与手动 Hex 使用不同宿主。
            // Read a bounded header for format and binary-content detection: dedicated formats route by
            // magic, while other clear binary data consistently uses HexEditorView instead of FileView's
            // embedded fallback viewer.
            byte[]? header = null;
            try
            {
                header = UotanStudio.Firmware.SharedKernel.FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            }
            catch
            {
                // 头部读取失败（文件被占用等）时退回纯扩展名解析。
                // Fall back to extension-only resolution when the header cannot be read.
            }

            mode = FileOpenMode.ResolveMode(path, header);
        }

        if (mode == EditorOpenMode.Log)
        {
            await OpenLogAsync(path);
            return;
        }

        // 设备树二进制：走 DTB 双栏编辑器。
        // Device-tree blobs open in the DTB dual-pane editor.
        if (mode == EditorOpenMode.Dtb)
        {
            await OpenDtbAsync(path);
            return;
        }

        // DEX 二进制：走 DEX 双栏编辑器（结构树 + 反汇编）。
        // DEX blobs open in the DEX dual-pane editor (structure tree + disassembly).
        if (mode == EditorOpenMode.Dex)
        {
            await OpenDexAsync(path);
            return;
        }

        // AXML（AndroidManifest.xml 等二进制 XML）：走 Manifest 编辑器。
        // AXML (AndroidManifest.xml and other binary XML): open in the Manifest editor.
        if (mode == EditorOpenMode.Axml)
        {
            await OpenManifestAsync(path);
            return;
        }

        // ARSC（resources.arsc）：走 ARSC 资源表编辑器。
        // ARSC (resources.arsc): open in the ARSC resource-table editor.
        if (mode == EditorOpenMode.Arsc)
        {
            await OpenArscAsync(path);
            return;
        }

        // 十六进制：走专用 Hex 编辑器（HexView 控件）。
        // Hex: open in the dedicated Hex editor (HexView control).
        if (mode == EditorOpenMode.Hex)
        {
            await OpenHexAsync(path);
            return;
        }

        // APK 包：走 APK 元数据编辑器（图标/包名/版本/权限/ABI）。
        // APK packages open in the APK metadata editor (icon/package/version/permissions/ABIs).
        if (mode == EditorOpenMode.Apk)
        {
            await OpenApkAsync(path);
            return;
        }

        // 固件元数据面板：扩展名命中格式处理器注册表（.img/.pac/.ops/.nb0 等）。
        // Firmware metadata panel: extensions registered by the format handlers (.img/.pac/.ops/.nb0 etc.).
        if (mode == EditorOpenMode.Firmware)
        {
            await OpenFirmwareAsync(path);
            return;
        }

        // 文件系统镜像（ext4/EROFS/F2FS raw img）：走文件管理器文档编辑器（内部目录树）。
        // Filesystem images (ext4/EROFS/F2FS raw img) open in the file-manager document editor
        // (internal directory tree).
        if (mode == EditorOpenMode.FileSystem)
        {
            await OpenFileSystemAsync(path);
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
            // 已打开文档：若请求方式不同则切换视图模式并重载。
            // Already-open document: switch view mode and reload when a different mode is requested.
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

        // 内容加载完成后补发活动文档消息，使属性面板刷新出行数/字符数/编码等真实值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文本尚未就绪，属性为缺省值）。
        // Re-send the active-document message after content loads so the properties panel shows
        // the real line/char counts and encoding (the first refresh fired before the text was ready).
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
    public async Task OpenFirmwareAsync(string path)
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
            await document.LoadAsync(fullPath);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Workspace",
                $"Failed to open firmware info for '{fullPath}': {ex.Message}");
        }

        // 固件信息无法解析（格式未识别或元数据读取失败）时回退十六进制视图：
        // 移除刚创建的固件文档并改用 Hex 编辑器打开，仅对本次新建且失败的文档生效。
        // When firmware info cannot be parsed (unrecognized format or metadata read failure), fall back
        // to the hex view: remove the just-created firmware document and reopen it in the Hex editor;
        // only applies to newly-created documents that failed to load.
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

        // 加载完成后补发活动文档消息，使属性面板刷新出真实属性值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文档数据尚未就绪）。
        // Re-send the active-document message after load so the properties panel shows real values
        // (the first refresh fired when the document data was not yet ready).
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

        // 按活动文档类型发送对应重载的消息，属性面板据此切换展示路径。
        // Send the matching message overload by document type so the properties panel can switch paths.
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
    /// Restores a previously open document tab from a persisted snapshot. Dispatches by
    /// <see cref="OpenDocumentDto.Kind"/> to the appropriate open method.
    /// </summary>
    /// <param name="dto"><para>文档描述符。</para>The document descriptor.</param>
    /// <param name="rootDir"><para>工程根目录（用于拼接绝对路径）。</para>The project root directory.</param>
    public async Task RestoreOpenDocumentAsync(Services.Projects.OpenDocumentDto dto, string rootDir)
    {
        if (dto.Kind == "Compare")
        {
            var compare = OpenCompareDocument();
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