using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;
using UotanStudio.Views.Workspace;

namespace UotanStudio.ViewModels.Workspace;

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
    private bool _subscribedDocumentDock;

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
            // 读取文件头参与内容探测：AXML（.xml 二进制）与 ARSC 按魔数路由到专用编辑器，
            // 否则 .xml 会被文本路由拦截、.arsc 落回 Auto 而无法自动打开对应视图。
            // Read the header for content sniffing: AXML (binary .xml) and ARSC route to their
            // dedicated editors by magic, otherwise .xml would be claimed by the text route and
            // .arsc would fall back to Auto instead of its dedicated view.
            byte[]? header = null;
            try
            {
                header = UotanStudio.Firmware.Common.FormatDetector.ReadHeader(path, 12);
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
    private DocumentDock? FindDocumentsPane()
    {
        if (Layout is not null && _factory.FindDockable(Layout, dockable => dockable.Id == "DocumentsPane") is DocumentDock documentDock)
        {
            return documentDock;
        }

        return null;
    }
}