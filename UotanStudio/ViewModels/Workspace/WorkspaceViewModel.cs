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
                header = UotanStudio.Firmware.Common.FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            }
            catch
            {
                // 头部读取失败（文件被占用等）时退回纯扩展名解析。
                // Fall back to extension-only resolution when the header cannot be read.
            }

            mode = FileOpenMode.ResolveMode(path, header);
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
            WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(document));
        }
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
    public async Task OpenDtbAsync(string path)
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
            .OfType<DtbEditorViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new DtbEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        await document.LoadAsync(fullPath);

        // 加载完成后补发活动文档消息，使属性面板刷新出真实属性值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文档数据尚未就绪）。
        // Re-send the active-document message after load so the properties panel shows real values
        // (the first refresh fired when the document data was not yet ready).
        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(dtbDocument: document));
        }
    }

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
    public async Task OpenDexAsync(string path)
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
            .OfType<DexEditorViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new DexEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        await document.LoadAsync(fullPath);

        // 加载完成后补发活动文档消息，使属性面板刷新出真实属性值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文档数据尚未就绪）。
        // Re-send the active-document message after load so the properties panel shows real values
        // (the first refresh fired when the document data was not yet ready).
        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(dexDocument: document));
        }
    }

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
    public async Task OpenManifestAsync(string path)
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
            .OfType<ManifestEditorViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new ManifestEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        await document.LoadAsync(fullPath);

        // 加载完成后补发活动文档消息，使属性面板刷新出真实属性值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文档数据尚未就绪）。
        // Re-send the active-document message after load so the properties panel shows real values
        // (the first refresh fired when the document data was not yet ready).
        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(manifestDocument: document));
        }
    }

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
    public async Task OpenArscAsync(string path)
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
            .OfType<ArscEditorViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new ArscEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        await document.LoadAsync(fullPath);

        // 加载完成后补发活动文档消息，使属性面板刷新出真实属性值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文档数据尚未就绪）。
        // Re-send the active-document message after load so the properties panel shows real values
        // (the first refresh fired when the document data was not yet ready).
        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(document));
        }
    }

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
    public async Task OpenHexAsync(string path)
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
            .OfType<HexEditorViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new HexEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        await document.LoadAsync(fullPath);

        // 加载完成后补发活动文档消息，使属性面板刷新出真实属性值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文档数据尚未就绪）。
        // Re-send the active-document message after load so the properties panel shows real values
        // (the first refresh fired when the document data was not yet ready).
        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(document));
        }
    }

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
    public async Task OpenApkAsync(string path)
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
            .OfType<ApkEditorViewModel>()
            .FirstOrDefault(doc => string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            documentDock.ActiveDockable = existing;
            return;
        }

        var document = new ApkEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        _factory.AddDockable(documentDock, document);
        documentDock.ActiveDockable = document;

        await document.LoadAsync(fullPath);

        // 加载完成后补发活动文档消息，使属性面板刷新出真实属性值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文档数据尚未就绪）。
        // Re-send the active-document message after load so the properties panel shows real values
        // (the first refresh fired when the document data was not yet ready).
        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(apkDocument: document));
        }
    }

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

        // 加载完成后补发活动文档消息，使属性面板刷新出真实属性值
        // （ActiveDockable 在加载前就已触发一次刷新，当时文档数据尚未就绪）。
        // Re-send the active-document message after load so the properties panel shows real values
        // (the first refresh fired when the document data was not yet ready).
        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(document));
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

        // 不发送 ActiveDocumentChangedMessage：属性面板没有文件管理器文档的展示路径，
        // 文档切换时由 OnDocumentDockPropertyChanged 的默认分支清空面板。
        // No ActiveDocumentChangedMessage is sent: the properties panel has no display path for
        // file-manager documents; OnDocumentDockPropertyChanged clears the panel via its default branch.
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
        var active = dock.ActiveDockable;
        switch (active)
        {
            case DtbEditorViewModel dtb:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(dtb));
                break;
            case DexEditorViewModel dex:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(dex));
                break;
            case ManifestEditorViewModel manifest:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(manifest));
                break;
            case ArscEditorViewModel arsc:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(arsc));
                break;
            case ApkEditorViewModel apk:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(apk));
                break;
            case FirmwareInfoViewModel firmware:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(firmware));
                break;
            case HexEditorViewModel hex:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(hex));
                break;
            case FileViewModel file:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage(file));
                break;
            default:
                WeakReferenceMessenger.Default.Send(new ActiveDocumentChangedMessage((FileViewModel?)null));
                break;
        }
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
    /// <para>释放资源：取消订阅文档区属性变更事件。</para>
    /// Releases resources: unsubscribes from document-dock property-changed events.
    /// </summary>
    public void Dispose()
    {
        if (_documentDock is not null)
        {
            _documentDock.PropertyChanged -= OnDocumentDockPropertyChanged;
        }

        _subscribedDocumentDock = false;
        GC.SuppressFinalize(this);
    }
}
