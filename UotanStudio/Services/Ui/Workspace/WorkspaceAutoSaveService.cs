using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Pipeline.Projects;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Settings;
using UotanStudio.ViewModels.Workspace;
using Ursa.Controls;

namespace UotanStudio.Services.Ui.Workspace;

internal sealed class WorkspaceAutoSaveService : IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly SettingsViewModel _settings;
    private readonly ProjectOperationsService _projects;
    private readonly AutoSaveCoordinator _coordinator;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly string _sessionPath;
    private bool _initialized;
    private bool _disposed;
    private Task? _initialization;
    private Window? _mainWindow;
    private MainWindowPlacement? _savedWindowPlacement;

    internal WorkspaceAutoSaveService(WorkspaceViewModel workspace, SettingsViewModel settings, ProjectOperationsService projects, string? sessionPath = null)
    {
        _workspace = workspace;
        _settings = settings;
        _projects = projects;
        _sessionPath = sessionPath ?? Path.Combine(Path.GetDirectoryName(SettingsPersistenceService.Instance.SettingsFilePath)!, "workspace-session.json");
        _coordinator = new AutoSaveCoordinator(SaveEditedDocumentsAsync) { Enabled = settings.AutoSave };
        _settings.PropertyChanged += OnSettingsChanged;
        _timer.Tick += OnTick;
        _projects.BeforeProjectChangeAsync = FlushAsync;
    }

    /// <summary>
    /// Lightweight signature of the last session snapshot; the whole DTO build/serialize
    /// pass is skipped when nothing changed.
    /// <para>上次会话快照的轻量签名；无变化时跳过整轮 DTO 构建与序列化。</para>
    /// </summary>
    private string? _lastSessionSignature;
    private Task? _deferredRestore;

    /// <summary>
    /// Computes a lightweight change signature of the session state: document paths, dock proportions
    /// and active items, visibility state, window bounds and node topology.
    /// <para>计算会话状态的轻量变更签名：文档路径、停靠比例与活动项、可见状态、窗口边界与节点拓扑。</para>
    /// </summary>
    /// <returns>
    /// The signature text for the state as currently captured.
    /// <para>按当前捕获状态生成的签名文本。</para>
    /// </returns>
    private string ComputeSessionSignature()
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append("project:").Append(_projects.CurrentProjectPath ?? "<none>").Append('|');
        var documents = WorkspaceSessionCapture.Documents(_workspace.Layout, null);
        sb.Append(documents.Count).Append('|');
        foreach (var document in documents)
            sb.Append(document.Kind).Append(':').Append(document.RelativePath).Append(':')
              .Append(document.IsActive ? 'a' : '-').Append(';');
        foreach (var dock in WorkspaceSessionCapture.Docks(_workspace.Layout))
            sb.Append(dock.Id).Append(':').Append(dock.Proportion?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "-")
              .Append(':').Append(dock.ActiveId ?? "-").Append(';');
        if (WorkspaceSessionCapture.ActiveWindow(_workspace.Layout, documents) is { } activeWindow)
            sb.Append("a:").Append(activeWindow.Kind).Append(':').Append(activeWindow.Identity)
              .Append(':').Append(activeWindow.DockId ?? "-").Append(';');
        foreach (var panel in WorkspacePanelService.Instance.CaptureVisibility().OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append("p:").Append(panel.Key).Append('=').Append(panel.Value ? '1' : '0').Append(';');
        if (_mainWindow is { } window)
            sb.Append("w:").Append(window.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                .Append(window.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                .Append(window.Position.X).Append(':').Append(window.Position.Y).Append(':').Append((int)window.WindowState);
        if (_projects.NodeEditorVM is { } nodes)
        {
            sb.Append('|').Append(nodes.OpenDocuments.Count);
            foreach (var document in nodes.OpenDocuments)
                sb.Append(':').Append(document.Name).Append('=').Append(document.Nodes.Count)
                    .Append(',').Append(document.Connections.Count);
            sb.Append(':').Append(nodes.Nodes.Count).Append(',').Append(nodes.Connections.Count)
                .Append(':').Append(nodes.Zoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(nodes.ShowMinimap ? '1' : '0');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Chooses which saved windows are brought back at startup. An explicit launch document suppresses session
    /// restore; otherwise a matching project restores everything, and any other case restores only windows that
    /// survive project switches (log, QCN, compare and screen mirror) so they do not depend on the last project.
    /// <para>选择启动时要恢复的窗口。存在显式启动文档时跳过会话恢复；匹配工程时恢复全部窗口；其余情况只恢复
    /// 不随工程切换关闭的窗口（日志、QCN、对比与投屏），使它们不再依赖上次打开的工程。</para>
    /// </summary>
    /// <param name="savedDocuments">Documents from the session snapshot. <para>会话快照中的文档。</para></param>
    /// <param name="currentProjectPath">Project already open at startup. <para>启动时已经打开的工程。</para></param>
    /// <param name="savedProjectPath">Project recorded in the snapshot. <para>快照中记录的工程。</para></param>
    /// <param name="hasStartupDocuments">Whether a launch document already populated the workspace.
    /// <para>启动文档是否已经填充了工作区。</para></param>
    /// <returns>The descriptors to restore, or <see langword="null"/> when none apply.
    /// <para>要恢复的描述符；无需恢复时为 <see langword="null"/>。</para></returns>
    internal static List<OpenDocumentDto>? SelectDocumentsForRestore(
        IReadOnlyList<OpenDocumentDto> savedDocuments,
        string? currentProjectPath,
        string? savedProjectPath,
        bool hasStartupDocuments)
    {
        if (hasStartupDocuments) return null;
        var projectMatches = currentProjectPath is not null && string.Equals(currentProjectPath, savedProjectPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (projectMatches) return [.. savedDocuments];

        var sessionOnly = savedDocuments
            .Where(d => DocumentOpenRegistry.ByKind(d.Kind) is { IsDocument: false })
            .ToList();
        return sessionOnly.Count > 0 ? sessionOnly : null;
    }

    /// <summary>
    /// Decides whether a proportion read from the session snapshot still leaves room for the pane's
    /// siblings along the split axis. A collapsed pane is recorded as <c>0</c> and its surviving
    /// sibling as <c>1</c>; restoring that pair keeps the sibling pinned to the whole axis and the
    /// document pane invisible, so such a value is dropped and the layout default applies.
    /// <para>判定会话快照中的比例是否仍为同级项留出空间。被折叠的面板记为 <c>0</c>，存活的同级记为
    /// <c>1</c>；还原这一对会把同级钉满整轴、使文档区不可见，因此丢弃该值并沿用布局默认比例。</para>
    /// </summary>
    /// <param name="dock">The dock receiving the persisted proportion. <para>接收持久化比例的停靠区。</para></param>
    /// <param name="proportion">The persisted proportion. <para>持久化比例。</para></param>
    /// <returns>
    /// <see langword="true"/> when another pane shares the axis; <see langword="false"/> when the saved
    /// value would starve it.
    /// <para>同级项共用该轴时为 <see langword="true"/>；保存值会挤掉同级项时为 <see langword="false"/>。</para>
    /// </returns>
    internal static bool LeavesRoomForSiblings(IDockable dock, double proportion)
    {
        ArgumentNullException.ThrowIfNull(dock);
        if (dock.Owner is not IDock { VisibleDockables: { } siblings })
        {
            return true;
        }

        var sharesAxis = siblings.Any(sibling => !ReferenceEquals(sibling, dock) && sibling is not ISplitter);
        return !sharesAxis || proportion < 1.0;
    }

    internal Task InitializeAsync() => _disposed ? Task.CompletedTask : _initialization ??= RestoreAsync();

    internal void AttachMainWindow(Window window)
    {
        _mainWindow = window;
    }

    private async Task RestoreAsync()
    {
        var resetPlacement = false;
        try
        {
            resetPlacement = RestoreWindowPlacement();
            if (_coordinator.Enabled && File.Exists(_sessionPath))
            {
                await using var input = File.OpenRead(_sessionPath);
                if (input.Length > 32 * 1024 * 1024) throw new InvalidDataException("Workspace session exceeds 32 MiB.");
                var snapshot = await JsonSerializer.DeserializeAsync(input, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot);
                if (snapshot is { Version: 1, Documents: not null, Panels: not null, Docks: not null })
                {
                    var hasStartupDocuments = WorkspaceSessionCapture.Documents(_workspace.Layout, null).Count > 0;
                    var documentsToRestore = SelectDocumentsForRestore(
                        snapshot.Documents, _projects.CurrentProjectPath, snapshot.ProjectPath, hasStartupDocuments);
                    if (documentsToRestore is { Count: > 0 })
                    {
                        var activeWindow = snapshot.ActiveWindow;
                        var foregroundIsDock = string.Equals(activeWindow?.Kind, "Dock", StringComparison.Ordinal);
                        var activeDescriptor = activeWindow is null ? null : DocumentOpenRegistry.ByKind(activeWindow.Kind);
                        var identityComparison = activeDescriptor?.PathIsIdentifier == true
                            ? StringComparison.Ordinal
                            : OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        var preferred = activeWindow is { Kind: { Length: > 0 } } && !foregroundIsDock
                            && documentsToRestore.Any(d => string.Equals(d.RelativePath, activeWindow.Identity, identityComparison))
                            ? activeWindow.Identity
                            : null;
                        await _workspace.RestoreDocumentsAsync(documentsToRestore, string.Empty, preferred,
                            restoreForegroundDocument: !foregroundIsDock);
                        _deferredRestore = _workspace.BackgroundRestore;
                    }
                    if (_projects.CurrentProjectPath is null)
                        WorkspacePanelService.Instance.RestoreVisibility(snapshot.Panels);
                    if (_projects.NodeEditorVM is { } nodes)
                    {
                        if (double.IsFinite(snapshot.NodeZoom)) nodes.Zoom = Math.Clamp(snapshot.NodeZoom, 0.2, 3);
                        nodes.ShowMinimap = snapshot.NodeMinimap;
                        if (_projects.CurrentProjectPath is null && snapshot.ProjectPath is null && nodes.Nodes.Count == 0 && snapshot.DraftWorkflow is { } draft)
                            await nodes.LoadFromAsync(draft);
                    }
                    if (_projects.CurrentProjectPath is null && snapshot.ProjectPath is null && snapshot.DraftPackingSettings is { } packing)
                        ProjectSettingViewModel.EnsureInstance().ApplyFromDto(packing);
                    var docks = WorkspaceSessionCapture.Enumerate(_workspace.Layout).OfType<IDock>().ToList();
                    foreach (var state in snapshot.Docks)
                    {
                        var dock = docks.FirstOrDefault(d => d.Id == state.Id);
                        if (dock is null) continue;
                        if (state.Proportion is > 0 and <= 1 && LeavesRoomForSiblings(dock, state.Proportion.Value))
                            dock.Proportion = state.Proportion.Value;
                        if (_projects.CurrentProjectPath is null && !string.IsNullOrEmpty(state.ActiveId)
                            && dock.VisibleDockables?.FirstOrDefault(d => d.Id == state.ActiveId) is { } active)
                            dock.ActiveDockable = active;
                    }
                    if (snapshot.ActiveWindow is { } foreground)
                    {
                        _workspace.ActivateWindow(foreground.Kind, foreground.Identity, foreground.DockId);
                        _workspace.UpdateDeferredRestoreForeground();
                    }
                }
            }
        }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "AutoSave", $"Restore workspace failed: {ex.Message}"); }
        finally
        {
            _initialized = true;
            if (!_disposed) _timer.Start();
        }
        if (resetPlacement && _mainWindow is { } window)
        {
            _ = ShowPlacementResetNoticeAsync(window);
        }
    }

    /// <summary>
    /// Waits for the deferred background document restore to finish so a save captures the fully restored layout
    /// instead of a partially populated one.
    /// <para>等待后台延迟文档还原完成，使保存捕获完整还原后的布局，而不是只捕获了部分窗口的中间状态。</para>
    /// </summary>
    private async Task WaitForDeferredRestoreAsync()
    {
        var pending = _deferredRestore;
        if (pending is null) return;
        try
        {
            await pending;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "AutoSave", $"Deferred restore wait failed: {ex.Message}");
        }
        finally
        {
            _deferredRestore = null;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.AutoSave))
            _coordinator.Enabled = _settings.AutoSave;
    }

    /// <summary>
    /// Applies the persisted main-window size, position and state; the placement lives in the application
    /// settings file and is therefore shared by every solution. A placement that leaves no usable visible part
    /// on any screen is discarded so the default window size and position stay in effect.
    /// <para>应用持久化的主窗口尺寸、位置与状态；该位置存放在应用设置文件中，因此对所有解决方案生效。若某处
    /// 持久化位置在任何屏幕上都留不下可用的可视部分，则丢弃该位置并保持默认窗口大小与位置。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the persisted placement was discarded as off-screen.
    /// <para>持久化位置因超出屏幕被丢弃时为 <see langword="true"/>。</para></returns>
    private bool RestoreWindowPlacement()
    {
        if (_mainWindow is not { } window) return false;
        if (SettingsPersistenceService.Instance.LoadSettings().MainWindowPlacement is not { } placement) return false;
        var width = placement.Width ?? window.Width;
        var height = placement.Height ?? window.Height;
        var x = placement.X ?? window.Position.X;
        var y = placement.Y ?? window.Position.Y;
        var scaling = window.Screens.Primary?.Scaling ?? 1;
        if (!WindowPlacementValidator.HasVisibleArea(x, y, width, height,
                [.. window.Screens.All.Select(screen => screen.WorkingArea)], scaling))
            return true;
        if (placement.Width is > 100 and < 10000 && placement.Height is > 100 and < 10000)
        {
            window.Width = placement.Width.Value;
            window.Height = placement.Height.Value;
        }
        if (placement.X is { } positionX && placement.Y is { } positionY)
            window.Position = new PixelPoint(positionX, positionY);
        if (placement.State != WindowState.Minimized)
            window.WindowState = placement.State;
        _savedWindowPlacement = placement;
        return false;
    }

    /// <summary>
    /// Tells the user that the stored window placement was outside every screen and that the default size and
    /// position are used instead.
    /// <para>告知用户已存窗口位置超出所有屏幕，因此改用默认的窗口大小与位置。</para>
    /// </summary>
    /// <param name="window">Owner window of the notice. <para>提示的所属窗口。</para></param>
    /// <returns>A task that completes after the notice closes. <para>提示关闭后完成的任务。</para></returns>
    private static async Task ShowPlacementResetNoticeAsync(Window window)
    {
        try
        {
            var language = LanguageService.Instance;
            await DialogService.ShowAsync(window,
                language.GetString("Window_RestoreOutOfScreenTitle"),
                language.GetString("Window_RestoreOutOfScreenMessage"),
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "AutoSave", $"Window placement notice failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the current main-window size, position and state to the application settings file, skipping the
    /// write when the persisted placement already matches.
    /// <para>把当前主窗口尺寸、位置与状态写入应用设置文件；磁盘上的位置已相同时跳过写入。</para>
    /// </summary>
    private void SaveWindowPlacement()
    {
        if (_mainWindow is not { } window) return;
        var placement = new MainWindowPlacement
        {
            Width = window.Width is > 100 and < 10000 ? window.Width : null,
            Height = window.Height is > 100 and < 10000 ? window.Height : null,
            X = window.Position.X,
            Y = window.Position.Y,
            State = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState
        };
        if (_savedWindowPlacement is { } saved && saved.Matches(placement)) return;
        if (SettingsPersistenceService.Instance.SaveMainWindowPlacement(placement)) _savedWindowPlacement = placement;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (!_initialized || _projects.IsBusy || _projects.IsImageOperationRunning) return;
        if (_coordinator.Enabled) await _coordinator.TickAsync();
        else SaveWindowPlacement();
    }

    private async Task SaveEditedDocumentsAsync()
    {
        if (!_initialized || _projects.IsBusy || _projects.IsImageOperationRunning) return;
        try
        {
            await _projects.AutoSaveAsync(persistProject: true, persistPageState: true);
            await SaveSessionAsync();
        }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "AutoSave", ex.Message); }
    }

    internal async Task SaveSessionAsync()
    {
        if (!_initialized || _projects.IsBusy) return;
        try
        {
            await WaitForDeferredRestoreAsync();
            SaveWindowPlacement();
            var signature = ComputeSessionSignature();
            if (string.Equals(signature, _lastSessionSignature, StringComparison.Ordinal)) return;

            var documents = WorkspaceSessionCapture.Documents(_workspace.Layout, null);
            var snapshot = new WorkspaceSessionSnapshot
            {
                ProjectPath = _projects.CurrentProjectPath,
                Panels = WorkspacePanelService.Instance.CaptureVisibility(),
                Documents = documents,
                Docks = WorkspaceSessionCapture.Docks(_workspace.Layout),
                ActiveWindow = WorkspaceSessionCapture.ActiveWindow(_workspace.Layout, documents)
            };
            if (_projects.NodeEditorVM is { } nodes)
            {
                snapshot.NodeZoom = nodes.Zoom;
                snapshot.NodeMinimap = nodes.ShowMinimap;
                if (_projects.CurrentProjectPath is null)
                {
                    snapshot.DraftWorkflow = new()
                    {
                        Nodes = WorkflowPersistenceService.BuildNodeDtos(nodes.Nodes),
                        Connections = WorkflowPersistenceService.BuildConnectionDtos(nodes.Nodes, nodes.Connections)
                    };
                    snapshot.DraftPackingSettings = ProjectSettingViewModel.EnsureInstance().ToDto();
                }
            }
            await AtomicJsonFile.WriteIfChangedAsync(_sessionPath, snapshot, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot);
            _lastSessionSignature = signature;
        }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "AutoSave", $"Save workspace failed: {ex.Message}"); }
    }

    internal async Task<bool> FlushAsync()
    {
        _timer.Stop();
        try
        {
            if (!await WaitAndConfirmDocumentsAsync())
                return false;
            if (_initialized && _projects.CurrentProjectPath is { Length: > 0 } path)
                await _projects.PersistToAsync(path, persistPageState: true);
            await SaveSessionAsync();
            return true;
        }
        finally { Resume(); }
    }

    internal void Resume()
    {
        if (_initialized && !_disposed) _timer.Start();
    }

    internal async Task<bool> SaveOnCloseAsync()
    {
        _timer.Stop();
        if (!await WaitAndConfirmDocumentsAsync())
        {
            Resume();
            return false;
        }
        SaveWindowPlacement();
        if (_initialized)
        {
            if (_projects.CurrentProjectPath is { Length: > 0 } path)
                await _projects.PersistToAsync(path);
            await SaveSessionAsync();
        }
        return string.IsNullOrEmpty(_projects.CurrentProjectPath) || await _projects.CloseAfterSaveAsync();
    }

    /// <summary>
    /// Waits for in-flight work to settle before prompting, and initiates no save before confirmation.
    /// <para>先等待进行中的工作结束再提示，确认之前不发起任何保存。</para>
    /// </summary>
    /// <returns>
    /// True when each dirty document's confirmation was accepted.
    /// <para>每个脏文档的确认均被接受时为真。</para>
    /// </returns>
    private async Task<bool> WaitAndConfirmDocumentsAsync()
    {
        await _coordinator.WaitForIdleAsync();
        await _projects.WaitForIdleAsync();
        return await DocumentSaveService.ConfirmAllAsync(WorkspaceSessionCapture.Enumerate(_workspace.Layout));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _settings.PropertyChanged -= OnSettingsChanged;
        if (_projects.BeforeProjectChangeAsync == FlushAsync)
            _projects.BeforeProjectChangeAsync = null;
    }
}