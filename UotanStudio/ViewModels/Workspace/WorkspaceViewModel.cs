using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// Workspace view model, hosting the dock layout and managing opening and focusing documents in the
/// document area.
/// <para>工作区视图模型，承载 Dock 布局，并管理文档区文档的打开与聚焦。</para>
/// </summary>
public class WorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly IFactory _factory;
    private readonly Dictionary<string, ScreenMirrorViewModel> _screenMirrorDocuments = new(StringComparer.Ordinal);
    private DocumentDock? _documentDock;
    private bool _subscribedDocumentDock;
    private CompareViewModel? _compareDocument;
    private Task? _backgroundRestore;
    private int _restoreGeneration;

    /// <summary>
    /// The window that was in front before a deferred restore started. Deferred documents are opened without
    /// activation, so it only needs to be restored once, after the last deferred document is in place.
    /// <para>延迟还原开始前处于前台的窗口。延迟文档打开时都不激活，因此只需在最后一个延迟文档就位后恢复一次。</para>
    /// </summary>
    private IDockable? _restoreForeground;

    /// <summary>
    /// Completion of the deferred document restore. The foreground window is restored before this task is created;
    /// persistence waits for it before rewriting the session so a partially restored layout is not captured.
    /// <para>延迟文档还原的完成任务。前台窗口会先于该任务还原；持久化在重写会话前等待它，避免捕获部分还原的布局。</para>
    /// </summary>
    internal Task? BackgroundRestore => _backgroundRestore;

    internal ProjectExplorerViewModel? ProjectExplorer => (_factory as IWorkspaceDockFactory)?.ProjectExplorer as ProjectExplorerViewModel;
    /// <summary>
    /// Exposes the concrete workspace factory for project-lifecycle document cleanup.
    /// <para>供工程生命周期清理文档使用。</para>
    /// </summary>
    internal IWorkspaceDockFactory? DockFactory => _factory as IWorkspaceDockFactory;

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
    /// Constructs the workspace view model around the supplied dock factory: creates the layout and
    /// initializes it.
    /// <para>围绕给定的 Dock 工厂构造工作区视图模型：创建布局并完成初始化。</para>
    /// </summary>
    public WorkspaceViewModel(IFactory factory)
    {
        _factory = factory;
        Layout = _factory.CreateLayout();
        if (Layout != null)
        {
            _factory.InitLayout(Layout);
        }
    }

    /// <summary>
    /// Resets the workspace dock layout back to its initial default arrangement, closing floating windows,
    /// restoring default panel visibility and proportions, and keeping open documents in the main document pane.
    /// <para>将工作区停靠布局强制还原为软件默认初始状态：关闭浮动窗口、恢复各面板默认可见性与比例，并将已打开文档保留在主文档区。</para>
    /// </summary>
    public void ResetLayout()
    {
        if (DockFactory is not { } factory)
        {
            return;
        }

        Layout = factory.ResetToDefaultLayout(Layout);
        if (FindDocumentsPane() is { } documentsPane)
        {
            EnsureDocumentDockSubscribed(documentsPane);
        }
    }

    /// <summary>
    /// Opens or focuses the compare document, reusing its instance until closed, and navigates to the workspace.
    /// <para>在工作区文档区打开或聚焦对比文档，复用尚未关闭的实例并切换到工作区。</para>
    /// </summary>
    /// <returns>The compare document, or null if the layout is unavailable. <para>对比文档；布局未就绪时为 null。</para></returns>
    public CompareViewModel? OpenCompareDocument() => OpenCompareDocument(activate: true);

    /// <summary>
    /// Opens or focuses the compare document, optionally without touching the foreground window.
    /// <para>打开或聚焦对比文档；可选择不改动前台窗口。</para>
    /// </summary>
    /// <param name="activate">Whether the compare document becomes the foreground window.
    /// <para>对比文档是否成为前台窗口。</para></param>
    /// <returns>The compare document, or null if the layout is unavailable. <para>对比文档；布局未就绪时为 null。</para></returns>
    public CompareViewModel? OpenCompareDocument(bool activate)
    {
        if (_compareDocument?.Owner is not IDock owner || owner.VisibleDockables?.Contains(_compareDocument) != true)
        {
            var documentDock = FindDocumentsPane();
            if (documentDock is null)
            {
                return null;
            }

            EnsureDocumentDockSubscribed(documentDock);
            _compareDocument = new CompareViewModel
            {
                Id = "CompareDocument",
                CanClose = true
            };
            _factory.AddDockable(documentDock, _compareDocument);
        }

        if (activate)
        {
            _factory.SetActiveDockable(_compareDocument);
            if (_factory.FindRoot(_compareDocument) is { } root)
            {
                _factory.SetFocusedDockable(root, _compareDocument);
            }

            WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
        }

        return _compareDocument;
    }

    /// <summary>
    /// Opens a new compare document in the workspace document area instead of reusing an existing one.
    /// <para>在工作区文档区新建一个对比文档，不复用已有实例。</para>
    /// </summary>
    /// <returns>The new compare document, or null if the layout is unavailable. <para>新建的对比文档；布局未就绪时为 null。</para></returns>
    public CompareViewModel? OpenNewCompareDocument() => OpenNewCompareDocument(activate: true);

    /// <summary>
    /// Opens a new compare document, optionally without touching the foreground window.
    /// <para>新建对比文档；可选择不改动前台窗口。</para>
    /// </summary>
    /// <param name="activate">Whether the compare document becomes the foreground window.
    /// <para>对比文档是否成为前台窗口。</para></param>
    /// <returns>The new compare document, or null if the layout is unavailable. <para>新建的对比文档；布局未就绪时为 null。</para></returns>
    public CompareViewModel? OpenNewCompareDocument(bool activate)
    {
        var documentDock = FindDocumentsPane();
        if (documentDock is null)
        {
            return null;
        }

        EnsureDocumentDockSubscribed(documentDock);
        var document = new CompareViewModel
        {
            Id = $"CompareDocument{Guid.NewGuid():N}",
            CanClose = true
        };
        _factory.AddDockable(documentDock, document);
        if (activate)
        {
            _factory.SetActiveDockable(document);
            if (_factory.FindRoot(document) is { } root)
            {
                _factory.SetFocusedDockable(root, document);
            }

            WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
        }

        return document;
    }

    /// <summary>
    /// Opens or focuses the screen-mirror document for one device. Each device keeps its own document,
    /// so several mirrors can stay open side by side; reopening the same device focuses its existing
    /// document instead of creating a duplicate. Slots whose document is not shown in its dock
    /// are dropped first, so reopening a closed device builds a fresh document.
    /// <para>为单个设备打开或聚焦投屏文档。每个设备各自保留一个文档，因此可以同时打开多个投屏；
    /// 重新打开同一设备时聚焦已有文档而不是创建重复文档。已关闭、未显示在停靠区的槽位会被先行
    /// 丢弃，因此重新打开已关闭设备时会新建文档。</para>
    /// </summary>
    /// <param name="deviceKey">Stable device serial or connect key; null or blank selects the
    /// untargeted document slot. <para>稳定的设备序列号或连接键；null 或空白选择无目标的文档槽位。</para></param>
    /// <returns>The screen-mirror document, or null if the layout is unavailable. <para>投屏文档；布局未就绪时为 null。</para></returns>
    public ScreenMirrorViewModel? OpenScreenMirrorDocument(string? deviceKey = null)
        => OpenScreenMirrorDocument(deviceKey, activate: true);

    internal ScreenMirrorViewModel? OpenScreenMirrorDocument(string? deviceKey, bool activate)
    {
        var key = string.IsNullOrWhiteSpace(deviceKey) ? string.Empty : deviceKey.Trim();
        PruneClosedScreenMirrorDocuments();
        if (!_screenMirrorDocuments.TryGetValue(key, out var document))
        {
            var documentDock = FindDocumentsPane();
            if (documentDock is null)
            {
                return null;
            }

            EnsureDocumentDockSubscribed(documentDock);
            document = new ScreenMirrorViewModel
            {
                Id = key.Length == 0 ? "ScreenMirrorDocument" : $"ScreenMirrorDocument:{key}",
                DeviceKey = key,
                Title = key.Length == 0
                    ? LanguageService.Instance.GetString("Tab_ScreenMirror")
                    : string.Format(LanguageService.Instance.GetString("Tab_ScreenMirrorDevice"), key),
                CanClose = true
            };
            _factory.AddDockable(documentDock, document);
            _screenMirrorDocuments[key] = document;
        }

        if (activate)
        {
            FocusDocument(document);
        }

        return document;
    }

    private static bool IsScreenMirrorDocumentLive(ScreenMirrorViewModel document)
        => document.Owner is IDock owner && owner.VisibleDockables?.Contains(document) == true;

    private void PruneClosedScreenMirrorDocuments()
    {
        if (_screenMirrorDocuments.Count == 0)
        {
            return;
        }

        List<string>? stale = null;
        foreach (var pair in _screenMirrorDocuments)
        {
            if (!IsScreenMirrorDocumentLive(pair.Value))
            {
                (stale ??= []).Add(pair.Key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var key in stale)
        {
            if (!_screenMirrorDocuments.Remove(key, out var closed))
            {
                continue;
            }

            try
            {
                closed.Cleanup();
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Warn, "Workspace",
                    $"Stopping closed mirror document '{closed.Id}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void FocusDocument(ScreenMirrorViewModel document)
    {
        _factory.SetActiveDockable(document);
        if (_factory.FindRoot(document) is { } root)
        {
            _factory.SetFocusedDockable(root, document);
        }
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
    }

    /// <summary>
    /// Opens a document from the unified request, delegating to the path+mode overload.
    /// <para>按统一文档打开请求打开文档，转调路径+模式重载。</para>
    /// </summary>
    /// <param name="request">The unified open request. <para>统一打开请求。</para></param>
    internal Task OpenDocumentAsync(DocumentOpenRequest request)
        => OpenDocumentAsync(request.Path ?? string.Empty, request.Mode);

    /// <summary>Asynchronously opens the document at the given path: focuses it if the path is already open in the document area; otherwise creates a <see cref="FileViewModel"/>, resolves the syntax language by extension, adds it to the document area and activates it.
    /// <para>异步打开指定路径的文档：若该路径已在文档区打开则聚焦并返回；否则创建 <see cref="FileViewModel"/>，按扩展名解析语法语言，加入文档区并激活。</para></summary>
    /// <param name="path">The path of the document file to open.<para>要打开的文档文件路径。</para></param>
    /// <param name="mode">The requested open mode; default <c>Auto</c> decided by file type+extension.
    /// <para>请求的打开方式；默认 <c>Auto</c> 由文件类型+后缀综合判定。</para></param>
    public Task OpenDocumentAsync(string path, EditorOpenMode mode = EditorOpenMode.Auto)
        => OpenDocumentAsync(path, mode, activate: true);

    /// <summary>
    /// Opens the resolved document; when <paramref name="activate"/> is false the foreground window is left untouched.
    /// <para>打开解析后的文档；<paramref name="activate"/> 为 false 时不改动前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="mode">The requested open mode. <para>请求的打开方式。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the open. <para>打开完成的完成任务。</para></returns>
    internal async Task OpenDocumentAsync(string path, EditorOpenMode mode, bool activate)
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
                header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            }
            catch
            {

            }

            FirmwareFormatRegistry.RegisterAll();
            if (header is null || !header.AsSpan().StartsWith("\u007fELF"u8))
            {
                detection = await Task.Run(() => FormatDetector.DetectDetailed(path)).ConfigureAwait(true);
            }
            mode = DocumentOpenRegistry.ResolveMode(path, header, detection);
        }

        var descriptor = DocumentOpenRegistry.ByMode(mode);
        if (descriptor?.Open is { } open)
        {
            await open(this, new DocumentOpenRequest
            {
                Path = path,
                Mode = mode,
                Detection = detection,
                Activate = activate
            });
            return;
        }

        await OpenTextAsync(path, mode, activate);
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
    internal Task OpenTextAsync(string path, EditorOpenMode mode) => OpenTextAsync(path, mode, activate: true);

    /// <summary>
    /// Opens or focuses a document through the text fallback path; when <paramref name="activate"/> is false
    /// the document is added without changing the foreground window.
    /// <para>以文本兜底路径打开或聚焦文档；<paramref name="activate"/> 为 false 时加入文档但不改动前台窗口。</para>
    /// </summary>
    /// <param name="path">The document file path. <para>文档文件路径。</para></param>
    /// <param name="mode">The resolved open mode. <para>解析后的打开方式。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    internal async Task OpenTextAsync(string path, EditorOpenMode mode, bool activate)
    {
        var documentDock = FindDocumentsPane();
        if (documentDock is null)
        {
            return;
        }

        EnsureDocumentDockSubscribed(documentDock);

        var fullPath = Path.GetFullPath(path);
        var existing = FindOpenDocument<FileViewModel>(doc =>
            string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {

            if (mode != EditorOpenMode.Auto && existing.OpenMode != mode)
            {
                existing.OpenMode = mode;
                await existing.LoadContentAsync(fullPath, mode);
            }

            if (activate)
            {
                ActivateDockable(existing, documentDock);
            }

            return;
        }

        var document = new FileViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath,
            Language = SyntaxLanguageRegistry.GetLanguageId(fullPath) ?? string.Empty,
            OpenMode = mode
        };

        AddDocument(documentDock, document, activate);

        await document.LoadContentAsync(fullPath, mode);

        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            Services.Ui.Workspace.DocumentNotifications.Publish(document);
        }
    }

    /// <summary>Opens or focuses a MediaTek LK bootloader image in the LK workbench.
    /// <para>在 LK 工作台中打开或聚焦 MediaTek LK 引导镜像。</para></summary>
    /// <param name="path">Image path.<para>镜像路径。</para></param>
    /// <param name="activate">Whether the document becomes active.<para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load.<para>载入完成的完成任务。</para></returns>
    internal Task OpenLkImageAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new LkImageViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

    /// <summary>Opens or focuses a MediaTek preloader boot region in the preloader workbench.
    /// <para>在预加载器工作台中打开或聚焦 MediaTek 预加载器引导区。</para></summary>
    /// <param name="path">Region path.<para>区域路径。</para></param>
    /// <param name="activate">Whether the document becomes active.<para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load.<para>载入完成的完成任务。</para></returns>
    internal Task OpenMtkPreloaderAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new MtkPreloaderViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

    /// <summary>
    /// Opens or focuses an offline log document.
    /// <para>打开或聚焦离线日志文档。</para>
    /// </summary>
    public Task OpenLogAsync(string path) => OpenLogAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses an offline log document, optionally without touching the foreground window.
    /// <para>打开或聚焦离线日志文档；可选择不改动前台窗口。</para>
    /// </summary>
    /// <param name="path">The log file path. <para>日志文件路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    internal async Task OpenLogAsync(string path, bool activate)
    {
        var documentDock = FindDocumentsPane();
        if (documentDock is null || string.IsNullOrWhiteSpace(path)) return;
        EnsureDocumentDockSubscribed(documentDock);
        var fullPath = Path.GetFullPath(path);
        var existing = FindOpenDocument<LogEditorViewModel>(document =>
            string.Equals(document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (activate)
            {
                ActivateDockable(existing, documentDock);
            }

            return;
        }
        var document = new LogEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath };
        AddDocument(documentDock, document, activate);
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
    public Task OpenDtbAsync(string path) => OpenDtbAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenDtbAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new DtbEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

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
    public Task OpenDexAsync(string path) => OpenDexAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenDexAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new DexEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

    /// <summary>Opens or activates an ELF information document for the supplied file.
    /// <para>为给定文件打开或激活 ELF 信息文档。</para>
    /// </summary>
    public Task OpenElfAsync(string path) => OpenElfAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenElfAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ElfInfoViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

    /// <summary>Opens or activates Qualcomm static image analysis for a local file.
    /// <para>为本地文件打开或激活 Qualcomm 镜像静态分析。</para></summary>
    /// <param name="path">Local image path. <para>本地镜像路径。</para></param>
    /// <returns>Completes when analysis has loaded. <para>分析加载完成后结束。</para></returns>
    public Task OpenQualcommAsync(string path) => OpenQualcommAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenQualcommAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new QualcommImageViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

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
    public Task OpenQcnAsync(string path) => OpenQcnAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenQcnAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new QcnEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

    /// <summary>
    /// Opens the given RKDevTool download-list configuration in the read-only configuration browser:
    /// focuses it if already open; otherwise creates and loads a <see cref="RockchipConfigEditorViewModel"/>.
    /// <para>在只读配置浏览视图中打开指定的 RKDevTool 下载列表配置：若已打开则聚焦并返回；
    /// 否则创建并加载 <see cref="RockchipConfigEditorViewModel"/>。</para>
    /// </summary>
    /// <param name="path">Path of the configuration file.<para>配置文件路径。</para></param>
    /// <returns>Completes when the document has loaded.<para>文档载入完成后结束。</para></returns>
    public Task OpenRockchipConfigAsync(string path) => OpenRockchipConfigAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenRockchipConfigAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new RockchipConfigEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

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
    public Task OpenManifestAsync(string path) => OpenManifestAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenManifestAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ManifestEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

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
    public Task OpenArscAsync(string path) => OpenArscAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenArscAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ArscEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

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
    public Task OpenHexAsync(string path) => OpenHexAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenHexAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new HexEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

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
    public Task OpenApkAsync(string path) => OpenApkAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenApkAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ApkEditorViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

    /// <summary>
    /// Opens or focuses a picture in the image viewer: the document reads its attributes through the
    /// SIMD codec facade and publishes them to the properties panel.
    /// <para>在图片查看器中打开或聚焦图片：文档经 SIMD 编解码门面读取属性并发布到属性面板。</para>
    /// </summary>
    /// <param name="path">Picture file path.<para>图片文件路径。</para></param>
    public Task OpenImageAsync(string path) => OpenImageAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenImageAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new ImageViewerViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

    /// <summary>
    /// Opens or focuses a logo/splash partition in the logo partition workbench, which lists the
    /// embedded images and offers preview, extraction and related-partition opening.
    /// <para>在 Logo 分区工作台中打开或聚焦 Logo/splash 分区，列出内嵌图片并提供预览、提取与相关分区打开。</para>
    /// </summary>
    /// <param name="path">Logo partition file path.<para>Logo 分区文件路径。</para></param>
    public Task OpenLogoPartitionAsync(string path) => OpenLogoPartitionAsync(path, activate: true);

    /// <summary>
    /// Opens or focuses the document for <paramref name="path"/>; when <paramref name="activate"/> is false it is
    /// added to the document area without becoming the foreground window.
    /// <para>打开或聚焦 <paramref name="path"/> 对应的文档；<paramref name="activate"/> 为 false 时加入文档区但不成为前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    /// <returns>Completion of the load. <para>载入完成的完成任务。</para></returns>
    internal Task OpenLogoPartitionAsync(string path, bool activate) => OpenOrActivateDocumentAsync(
        path, static document => document.FilePath,
        static fullPath => new LogoPartitionViewModel { Title = Path.GetFileName(fullPath), FilePath = fullPath },
        static (document, fullPath) => document.LoadAsync(fullPath), activate);

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
    public Task OpenFirmwareAsync(string path, FormatDetectionResult? detection = null)
        => OpenFirmwareAsync(path, detection, activate: true);

    /// <summary>
    /// Opens a firmware file in the metadata panel, optionally without touching the foreground window.
    /// <para>在元数据面板打开固件文件；可选择不改动前台窗口。</para>
    /// </summary>
    /// <param name="path">The firmware path. <para>固件路径。</para></param>
    /// <param name="detection">An already resolved detection result. <para>已完成的格式检测结果。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    internal async Task OpenFirmwareAsync(string path, FormatDetectionResult? detection, bool activate)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var documentDock = FindDocumentsPane();
        if (documentDock is null)
        {
            return;
        }

        EnsureDocumentDockSubscribed(documentDock);

        var fullPath = Path.GetFullPath(path);
        var existing = FindOpenDocument<FirmwareInfoViewModel>(doc =>
            string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (activate)
            {
                ActivateDockable(existing, documentDock);
            }

            return;
        }

        var document = new FirmwareInfoViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        AddDocument(documentDock, document, activate);

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

            await OpenHexAsync(path, activate);
            return;
        }

        if (ReferenceEquals(documentDock.ActiveDockable, document))
        {
            Services.Ui.Workspace.DocumentNotifications.Publish(document);
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
    public Task OpenFileSystemAsync(string path) => OpenFileSystemAsync(path, activate: true);

    /// <summary>
    /// Opens a filesystem image, optionally without touching the foreground window.
    /// <para>打开文件系统镜像；可选择不改动前台窗口。</para>
    /// </summary>
    /// <param name="path">The image path. <para>镜像路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    internal async Task OpenFileSystemAsync(string path, bool activate)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var documentDock = FindDocumentsPane();
        if (documentDock is null)
        {
            return;
        }

        EnsureDocumentDockSubscribed(documentDock);

        var fullPath = Path.GetFullPath(path);
        var existing = FindOpenDocument<FileManagerEditorViewModel>(doc =>
            string.Equals(doc.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (activate)
            {
                ActivateDockable(existing, documentDock);
            }

            return;
        }

        var document = new FileManagerEditorViewModel
        {
            Title = Path.GetFileName(fullPath),
            FilePath = fullPath
        };

        AddDocument(documentDock, document, activate);

        try
        {
            await document.LoadFromImageAsync(fullPath);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Workspace",
                $"Failed to open filesystem image '{fullPath}': {ex.Message}");
        }

        Services.Ui.Workspace.DocumentNotifications.Publish(document);
    }

    /// <summary>Opens a typed document and refreshes its properties after loading only while it remains active.</summary>
    internal Task OpenOrActivateDocumentAsync<TDocument>(string path,
        Func<TDocument, string?> getPath, Func<string, TDocument> create,
        Func<TDocument, string, Task> load) where TDocument : class, IDockable
        => OpenOrActivateDocumentAsync(path, getPath, create, load, activate: true);

    /// <summary>
    /// Opens a typed document; when <paramref name="activate"/> is false it is added to the document area
    /// without changing the foreground window.
    /// <para>打开一个具体类型的文档；<paramref name="activate"/> 为 false 时加入文档区但不改动前台窗口。</para>
    /// </summary>
    /// <param name="path">The document path. <para>文档路径。</para></param>
    /// <param name="getPath">Reads the dedup key from an existing document. <para>从已打开文档读取去重键。</para></param>
    /// <param name="create">Creates the document. <para>创建文档。</para></param>
    /// <param name="load">Loads the document from its path. <para>按路径载入文档。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    internal async Task OpenOrActivateDocumentAsync<TDocument>(string path,
        Func<TDocument, string?> getPath, Func<string, TDocument> create,
        Func<TDocument, string, Task> load, bool activate) where TDocument : class, IDockable
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var documentDock = FindDocumentsPane();
        if (documentDock is null) return;
        EnsureDocumentDockSubscribed(documentDock);
        var fullPath = Path.GetFullPath(path);
        var existing = FindOpenDocument<TDocument>(document =>
            string.Equals(getPath(document), fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (activate)
            {
                ActivateDockable(existing, documentDock);
            }

            return;
        }
        var document = create(fullPath);
        AddDocument(documentDock, document, activate);
        await load(document, fullPath);
        if (ReferenceEquals(documentDock.ActiveDockable, document))
            Services.Ui.Workspace.DocumentNotifications.Publish(document);
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
        if (_subscribedDocumentDock && ReferenceEquals(_documentDock, documentDock))
        {
            return;
        }

        if (_documentDock is not null)
        {
            _documentDock.PropertyChanged -= OnDocumentDockPropertyChanged;
        }

        _documentDock = documentDock;
        documentDock.PropertyChanged += OnDocumentDockPropertyChanged;
        _subscribedDocumentDock = true;
    }

    private void OnDocumentDockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IDock.ActiveDockable) || sender is not DocumentDock dock)
        {
            return;
        }

        Services.Ui.Workspace.DocumentNotifications.Publish(dock.ActiveDockable);
    }

    /// <summary>
    /// Finds and caches the document area (DocumentsPane) by Id in the layout; returns <c>null</c> when
    /// the layout is not ready.
    /// <para>在布局中按 Id 查找文档区（DocumentsPane）并缓存其引用；布局未就绪时返回 <c>null</c>。</para>
    /// </summary>
    public DocumentDock? FindDocumentsPanePublic() => FindDocumentsPane();

    private DocumentDock? FindDocumentsPane()
    {
        if (Layout is not null)
        {
            if (_documentDock is not null
                && ReferenceEquals(
                    _factory.FindDockable(Layout, dockable => ReferenceEquals(dockable, _documentDock)),
                    _documentDock))
            {
                return _documentDock;
            }

            if ((_factory.FindDockable(Layout, static dockable => dockable is DocumentDock && dockable.Id == "DocumentsPane")
                ?? _factory.FindDockable(Layout, static dockable => dockable is DocumentDock)) is DocumentDock documentDock)
            {
                EnsureDocumentDockSubscribed(documentDock);
                return documentDock;
            }
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
    public Task RestoreOpenDocumentAsync(UotanStudio.Pipeline.Projects.OpenDocumentDto dto, string rootDir)
        => RestoreOpenDocumentAsync(dto, rootDir, activate: true);

    internal async Task RestoreOpenDocumentAsync(UotanStudio.Pipeline.Projects.OpenDocumentDto dto, string rootDir, bool activate)
    {
        if (dto.Kind == "Compare")
        {
            var hasLiveCompare = _compareDocument?.Owner is IDock owner
                && owner.VisibleDockables?.Contains(_compareDocument) == true;
            var compare = !hasLiveCompare || _compareDocument!.IsEmptyDocument
                ? OpenCompareDocument(activate)
                : OpenNewCompareDocument(activate);
            if (compare is not null)
                foreach (var path in new[] { dto.CompareLeftPath, dto.CompareRightPath })
                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                        await compare.AddComparisonPathAsync(path);
            return;
        }

        var descriptor = DocumentOpenRegistry.ByKind(dto.Kind);
        if (descriptor?.Open is not { } open)
        {
            return;
        }

        if (descriptor.PathIsIdentifier)
        {
            try
            {
                await open(this, new DocumentOpenRequest
                {
                    Path = dto.RelativePath,
                    Serial = dto.RelativePath,
                    Activate = activate
                });
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Error, "Workspace",
                    $"Failed to restore window '{dto.RelativePath}' ({dto.Kind}): {ex.Message}");
            }

            return;
        }

        var fullPath = Path.Combine(rootDir, dto.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return;
        }

        var mode = Enum.IsDefined((EditorOpenMode)dto.OpenMode)
            ? (EditorOpenMode)dto.OpenMode
            : EditorOpenMode.Auto;

        try
        {
            await open(this, new DocumentOpenRequest
            {
                Path = fullPath,
                Mode = mode,
                Activate = activate
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Workspace",
                $"Failed to restore open document '{dto.RelativePath}' ({dto.Kind}): {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the recorded foreground window synchronously, then opens every other window at background
    /// dispatcher priority so decompilation, firmware parsing and similar heavy loads never block the UI.
    /// <para>同步还原记录的前台窗口，随后以后台调度优先级打开其余窗口，使反编译、固件解析等重负载不阻塞界面。</para>
    /// </summary>
    /// <param name="documents">The persisted document descriptors. <para>持久化的文档描述符。</para></param>
    /// <param name="rootDir">The project root used to resolve relative paths. <para>用于解析相对路径的工程根目录。</para></param>
    /// <param name="foregroundIdentity">Optional persisted identity of the foreground document; falls back to
    /// the descriptor's active flag. <para>前台文档的持久化标识；缺省时回退到描述符的活动标记。</para></param>
    /// <param name="restoreForegroundDocument">Whether one document must be restored synchronously; set to
    /// <see langword="false"/> when a tool pane was foreground so every document is deferred.
    /// <para>是否需要同步还原一个文档；前台为工具面板时传 <see langword="false"/>，让所有文档都延迟打开。</para></param>
    /// <returns>Completion of the foreground restore; deferred documents continue in the background.
    /// <para>前台窗口还原的完成任务；其余文档在后台继续还原。</para></returns>
    internal Task RestoreDocumentsAsync(
        IEnumerable<UotanStudio.Pipeline.Projects.OpenDocumentDto> documents,
        string rootDir,
        string? foregroundIdentity = null,
        bool restoreForegroundDocument = true)
    {
        var snapshot = documents.ToList();
        if (snapshot.Count == 0)
        {
            _backgroundRestore = null;
            return Task.CompletedTask;
        }

        _backgroundRestore = null;
        if (!restoreForegroundDocument)
        {
            var deferredGeneration = Interlocked.Increment(ref _restoreGeneration);
            _restoreForeground = FindActiveDockable();
            _backgroundRestore = RestoreDeferredOnceAsync(snapshot, rootDir, deferredGeneration);
            return Task.CompletedTask;
        }

        var preferred = foregroundIdentity is null ? null
            : snapshot.FirstOrDefault(d => string.Equals(d.RelativePath, foregroundIdentity, StringComparison.Ordinal));
        var foreground = preferred ?? snapshot.LastOrDefault(d => d.IsActive) ?? snapshot[0];
        return RestoreForegroundThenDeferAsync(snapshot, foreground, rootDir);
    }

    private async Task RestoreForegroundThenDeferAsync(
        List<UotanStudio.Pipeline.Projects.OpenDocumentDto> documents,
        UotanStudio.Pipeline.Projects.OpenDocumentDto foreground,
        string rootDir)
    {
        await RestoreOpenDocumentAsync(foreground, rootDir, activate: true);
        var generation = Interlocked.Increment(ref _restoreGeneration);
        var deferred = documents.Where(d => !ReferenceEquals(d, foreground)).ToList();
        _restoreForeground = FindActiveDockable();
        _backgroundRestore = deferred.Count == 0
            ? Task.CompletedTask
            : RestoreDeferredOnceAsync(deferred, rootDir, generation);
    }

    /// <summary>
    /// Opens every deferred document at background priority and restores the saved foreground window once at the
    /// end, so the restore produces a single focus change instead of one per document.
    /// <para>以后台优先级打开每个延迟文档，并在结束时只恢复一次保存的前台窗口，使还原只产生一次焦点变化，而不是每个文档一次。</para>
    /// </summary>
    /// <param name="documents">Deferred descriptors. <para>延迟的文档描述符。</para></param>
    /// <param name="rootDir">Project root. <para>工程根目录。</para></param>
    /// <param name="generation">Restore generation guarding cancellation. <para>用于取消守卫的还原世代号。</para></param>
    /// <returns>Completion of the deferred restore. <para>延迟还原的完成任务。</para></returns>
    private async Task RestoreDeferredOnceAsync(
        List<UotanStudio.Pipeline.Projects.OpenDocumentDto> documents,
        string rootDir,
        int generation)
    {
        await RestoreDeferredAsync(documents, rootDir, generation);
        if (generation != Volatile.Read(ref _restoreGeneration))
        {
            return;
        }

        if (Volatile.Read(ref _restoreForeground) is { } foreground)
        {
            Reactivate(foreground);
        }
    }

    private async Task RestoreDeferredAsync(
        List<UotanStudio.Pipeline.Projects.OpenDocumentDto> documents,
        string rootDir,
        int generation)
    {
        foreach (var document in documents)
        {
            if (generation != Volatile.Read(ref _restoreGeneration))
            {
                return;
            }

            try
            {
                Task? load = null;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    new Action(() => load = RestoreOpenDocumentAsync(document, rootDir, activate: false)),
                    Avalonia.Threading.DispatcherPriority.Background);
                if (load is not null)
                {
                    await load.ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Error, "Workspace",
                    $"Deferred restore of '{document.RelativePath}' ({document.Kind}) failed: {ex.Message}");
            }
        }
    }

    private IDockable? FindActiveDockable()
        => WorkspaceSessionCapture.Enumerate(Layout).OfType<IDock>()
            .Select(dock => dock.ActiveDockable)
            .FirstOrDefault(item => item is not null && item is not IDock && WorkspaceSessionCapture.IsPersisted(item));

    private void Reactivate(IDockable dockable)
    {
        if (dockable.Owner is not IDock owner || owner.VisibleDockables?.Contains(dockable) != true)
        {
            return;
        }

        owner.ActiveDockable = dockable;
        _factory.SetActiveDockable(dockable);
        if (_factory.FindRoot(dockable) is { } root)
        {
            _factory.SetFocusedDockable(root, dockable);
        }
    }

    internal void ActivateDockableById(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (WorkspaceSessionCapture.Enumerate(Layout).FirstOrDefault(d => d.Id == id) is { } dockable)
        {
            Reactivate(dockable);
        }
    }

    /// <summary>
    /// Focuses the window recorded as foreground in the session snapshot. Documents are matched by their persisted
    /// identity (path or mirror device key); tool panes by their stable dock id within the recorded dock.
    /// <para>聚焦会话快照中记录的前台窗口。文档按持久化标识（路径或投屏设备键）匹配，工具面板按记录停靠区内的稳定 Dock Id 匹配。</para>
    /// </summary>
    /// <param name="kind">The persisted window kind, or <c>Dock</c> for a tool pane. <para>持久化的窗口种类，工具面板为 <c>Dock</c>。</para></param>
    /// <param name="identity">The persisted document path/device key or the dockable id. <para>持久化的文档路径/设备键或停靠项 Id。</para></param>
    /// <param name="dockId">The owning dock id for a tool pane; ignored for documents. <para>工具面板所属的停靠区 Id；文档忽略此值。</para></param>
    internal void ActivateWindow(string kind, string identity, string? dockId)
    {
        if (string.Equals(kind, "Dock", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(identity)) return;

            var docks = WorkspaceSessionCapture.Enumerate(Layout).OfType<IDock>().ToList();
            var targetDock = dockId is null ? null : docks.FirstOrDefault(d => d.Id == dockId);
            var dockable = (targetDock?.VisibleDockables ?? docks.SelectMany(d => d.VisibleDockables ?? []))
                .FirstOrDefault(d => d.Id == identity);
            if (dockable is null) return;
            if (targetDock is not null) targetDock.ActiveDockable = dockable;
            Reactivate(dockable);
            return;
        }

        var descriptor = DocumentOpenRegistry.ByKind(kind);
        if (descriptor?.GetPath is not { } getPath) return;
        if (string.IsNullOrEmpty(identity) && !descriptor.AllowEmptyPath) return;
        var comparison = descriptor.PathIsIdentifier ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var document = WorkspaceSessionCapture.Enumerate(Layout)
            .FirstOrDefault(d => ReferenceEquals(DocumentOpenRegistry.ByInstance(d), descriptor)
                && string.Equals(getPath(d) ?? string.Empty, identity, comparison));
        if (document is not null) Reactivate(document);
    }

    /// <summary>
    /// Re-reads the window that must be in front once the deferred restore finishes. Called after the persisted
    /// foreground is applied so the last deferred document cannot become the visible tab.
    /// <para>重新读取延迟还原结束后应处于前台的窗口。应用持久化前台窗口后调用，避免最后一个延迟文档成为可见标签。</para>
    /// </summary>
    internal void UpdateDeferredRestoreForeground()
    {
        if (_backgroundRestore is not null)
        {
            Volatile.Write(ref _restoreForeground, FindActiveDockable());
        }
    }

    internal void CancelBackgroundRestore() => Interlocked.Increment(ref _restoreGeneration);

    internal Task OpenPartitionAsync(string path) => OpenPartitionAsync(path, activate: true);

    /// <summary>
    /// Opens a partition editor, optionally without touching the foreground window.
    /// <para>打开分区编辑器；可选择不改动前台窗口。</para>
    /// </summary>
    /// <param name="path">The partition path. <para>分区路径。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    internal async Task OpenPartitionAsync(string path, bool activate)
    {
        var dock = FindDocumentsPanePublic();
        if (dock is null) return;
        EnsureDocumentDockSubscribed(dock);
        var document = FindOpenDocument<PartitionEditorViewModel>(d => d.FilePath == path);
        if (document is null)
        {
            document = new PartitionEditorViewModel { Title = Path.GetFileName(path) };
            AddDocument(dock, document, activate);
            await document.LoadAsync(path);
        }
        else if (activate)
        {
            ActivateDockable(document, dock);
        }

        if (activate)
        {
            Services.Ui.Workspace.DocumentNotifications.Publish(document);
        }
    }

    private TDocument? FindOpenDocument<TDocument>(Func<TDocument, bool> predicate)
        where TDocument : class, IDockable
        => Services.Ui.Workspace.WorkspaceSessionCapture.Enumerate(Layout)
            .OfType<TDocument>()
            .FirstOrDefault(predicate);

    /// <summary>
    /// Adds a document to the document area without changing the foreground window when
    /// <paramref name="activate"/> is false, so a background session restore cannot pull focus.
    /// <para>把文档加入文档区；<paramref name="activate"/> 为 false 时不改动前台窗口，使后台会话还原无法抢走焦点。</para>
    /// </summary>
    /// <param name="documentDock">The document area. <para>文档区。</para></param>
    /// <param name="document">The document to add. <para>要加入的文档。</para></param>
    /// <param name="activate">Whether the document becomes active. <para>文档是否成为活动项。</para></param>
    private void AddDocument(DocumentDock documentDock, IDockable document, bool activate)
    {
        _factory.AddDockable(documentDock, document);
        if (activate)
        {
            documentDock.ActiveDockable = document;
        }
    }

    private void ActivateDockable(IDockable dockable, IDock fallbackDock)
    {
        if (dockable.Owner is IDock ownerDock)
        {
            ownerDock.ActiveDockable = dockable;
        }
        else
        {
            fallbackDock.ActiveDockable = dockable;
        }

        _factory.SetActiveDockable(dockable);
    }

    /// <summary>
    /// Releases resources: clears the mirror-document slots and detaches document-dock event handlers.
    /// <para>释放资源：清空投屏文档槽位并解除文档区的事件订阅。</para>
    /// </summary>
    public void Dispose()
    {
        CancelBackgroundRestore();
        CloseDocumentsForShutdown();
        ProjectExplorer?.Dispose();
        if (_factory is IWorkspaceDockFactory workspaceFactory) workspaceFactory.DisposeTerminal();
        if (_documentDock is not null)
        {
            _documentDock.PropertyChanged -= OnDocumentDockPropertyChanged;
        }

        _subscribedDocumentDock = false;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Runs every open view's own close logic before the layout goes away: mirror documents stop their mirror link,
    /// editors release their files and terminals, and every dockable the document registry knows is cleaned once.
    /// <para>在布局消失之前运行每个已打开视图自身的关闭逻辑：投屏文档停止投屏链路、编辑器释放文件与终端，
    /// 文档登记表中已知的每个停靠面板都只清理一次。</para>
    /// </summary>
    private void CloseDocumentsForShutdown()
    {
        var closed = new HashSet<IDockable>();
        foreach (var document in _screenMirrorDocuments.Values)
        {
            if (!closed.Add(document))
            {
                continue;
            }

            try
            {
                document.Cleanup();
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Warn, "Workspace",
                    $"Stopping mirror document '{document.Id}' during shutdown failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        _screenMirrorDocuments.Clear();

        foreach (var dockable in EnumerateDockables(Layout))
        {
            if (!closed.Add(dockable))
            {
                continue;
            }

            try
            {
                DocumentOpenRegistry.ByInstance(dockable)?.Cleanup?.Invoke(dockable);
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Warn, "Workspace",
                    $"Closing dockable '{dockable.Id}' during shutdown failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Walks the dock layout depth first, yielding every dockable below <paramref name="root"/>.
    /// <para>深度优先遍历停靠布局，产出 <paramref name="root"/> 下的每个停靠面板。</para>
    /// </summary>
    /// <param name="root">Layout root to walk. <para>要遍历的布局根。</para></param>
    /// <returns>Every dockable in the tree, root included. <para>树中的每个停靠面板（含根）。</para></returns>
    private static IEnumerable<IDockable> EnumerateDockables(IDockable? root)
    {
        if (root is null)
        {
            yield break;
        }

        yield return root;
        if (root is IDock dock && dock.VisibleDockables is { } children)
        {
            foreach (var child in children)
            {
                foreach (var descendant in EnumerateDockables(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}