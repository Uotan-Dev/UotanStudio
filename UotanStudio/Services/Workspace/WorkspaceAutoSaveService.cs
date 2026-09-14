using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using Dock.Model.Core;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Workspace;

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

    internal WorkspaceAutoSaveService(WorkspaceViewModel workspace, SettingsViewModel settings, ProjectOperationsService projects)
    {
        _workspace = workspace;
        _settings = settings;
        _projects = projects;
        _sessionPath = Path.Combine(Path.GetDirectoryName(SettingsPersistenceService.Instance.SettingsFilePath)!, "workspace-session.json");
        _coordinator = new AutoSaveCoordinator(SaveEditedDocumentsAsync) { Enabled = settings.AutoSave };
        _settings.PropertyChanged += OnSettingsChanged;
        _timer.Tick += OnTick;
        _projects.BeforeProjectChangeAsync = FlushAsync;
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);
    }

    /// <summary>
    /// <para>上次会话快照的轻量签名；无变化时跳过整轮 DTO 构建与序列化。</para>
    /// Lightweight signature of the last session snapshot; the whole DTO build/serialize
    /// pass is skipped when nothing changed.
    /// </summary>
    private string? _lastSessionSignature;

    /// <summary>
    /// <para>计算会话状态的轻量变更签名（文档路径集合、停靠比例/活动项、节点拓扑与缩放），
    /// 避免每轮定时器都全量重建 DTO 并序列化。</para>
    /// Computes a lightweight change signature of the session state (document path set, dock
    /// proportions/active items, panel visibility, main-window bounds, node topology and zoom) so the timer skips full DTO rebuilds
    /// and serialization when nothing changed.
    /// </summary>
    private string ComputeSessionSignature()
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append("project:").Append(_projects.CurrentProjectPath ?? "<none>").Append('|');
        var documents = WorkspaceSessionCapture.Documents(_workspace.Layout, Environment.CurrentDirectory);
        sb.Append(documents.Count).Append('|');
        foreach (var document in documents)
            sb.Append(document.Kind).Append(':').Append(document.RelativePath).Append(':')
              .Append(document.IsActive ? 'a' : '-').Append(';');
        foreach (var dock in WorkspaceSessionCapture.Docks(_workspace.Layout))
            sb.Append(dock.Id).Append(':').Append(dock.Proportion?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "-")
              .Append(':').Append(dock.ActiveId ?? "-").Append(';');
        foreach (var panel in WorkspacePanelService.Instance.CaptureVisibility().OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append("p:").Append(panel.Key).Append('=').Append(panel.Value ? '1' : '0').Append(';');
        if (_mainWindow is { } window)
            sb.Append("w:").Append(window.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                .Append(window.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                .Append(window.Position.X).Append(':').Append(window.Position.Y).Append(':').Append((int)window.WindowState);
        if (_projects.NodeEditorVM is { } nodes)
        {
            sb.Append('|').Append(nodes.Nodes.Count).Append(':').Append(nodes.Connections.Count)
                .Append(':').Append(nodes.Zoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(nodes.ShowMinimap ? '1' : '0');
        }
        return sb.ToString();
    }

    internal Task InitializeAsync() => _disposed ? Task.CompletedTask : _initialization ??= RestoreAsync();

    internal void AttachMainWindow(Window window)
    {
        _mainWindow = window;
    }

    private async Task RestoreAsync()
    {
        try
        {
            if (_coordinator.Enabled && File.Exists(_sessionPath))
            {
                await using var input = File.OpenRead(_sessionPath);
                if (input.Length > 32 * 1024 * 1024) throw new InvalidDataException("Workspace session exceeds 32 MiB.");
                var snapshot = await JsonSerializer.DeserializeAsync(input, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot);
                if (snapshot is { Version: 1, Documents: not null, Panels: not null, Docks: not null })
                {
                    var hasStartupDocuments = WorkspaceSessionCapture.Documents(_workspace.Layout, Environment.CurrentDirectory).Count > 0;
                    if (_projects.CurrentProjectPath is not null && !hasStartupDocuments && string.Equals(_projects.CurrentProjectPath, snapshot.ProjectPath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        await _workspace.RestoreDocumentsAsync(snapshot.Documents, string.Empty);
                    // 仅无工程时用全局 workspace-session.json 兜底面板可见性；有工程时以解决方案 State 的 panels 为准，
                    // 避免启动恢复覆盖打开解决方案时已按解决方案状态恢复的面板（如打包设置、终端）。
                    // The global workspace-session.json only falls back for panel visibility when no project is open;
                    // with a project open, the solution State's panels are authoritative so startup restore must not override them.
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
                    ApplyWindowState(snapshot);
                    var docks = WorkspaceSessionCapture.Enumerate(_workspace.Layout).OfType<IDock>().ToList();
                    foreach (var state in snapshot.Docks)
                    {
                        var dock = docks.FirstOrDefault(d => d.Id == state.Id);
                        if (dock is null) continue;
                        if (state.Proportion is > 0 and <= 1) dock.Proportion = state.Proportion.Value;
                        // 仅无工程时恢复活动面板：有工程时解决方案打开已恢复面板可见性，
                        // 活动面板保持用户当前所处位置，不让全局会话把左侧切到打包设置等面板上。
                        // Only restore the active panel when no project is open: with a project open the
                        // solution open already restored panel visibility, so the active panel must keep
                        // the user's current position instead of being switched by the global session.
                        if (_projects.CurrentProjectPath is null && !string.IsNullOrEmpty(state.ActiveId)
                            && dock.VisibleDockables?.FirstOrDefault(d => d.Id == state.ActiveId) is { } active)
                            dock.ActiveDockable = active;
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
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.AutoSave))
            _coordinator.Enabled = _settings.AutoSave;
    }

    private void ApplyWindowState(WorkspaceSessionSnapshot snapshot)
    {
        var window = _mainWindow;
        if (window is null) return;
        if (snapshot.WindowWidth is > 100 and < 10000 && snapshot.WindowHeight is > 100 and < 10000)
        {
            window.Width = snapshot.WindowWidth.Value;
            window.Height = snapshot.WindowHeight.Value;
        }
        if (snapshot.WindowX is { } x && snapshot.WindowY is { } y)
            window.Position = new PixelPoint(x, y);
        if (snapshot.WindowState is { } state && Enum.IsDefined(typeof(WindowState), state)
            && (WindowState)state != WindowState.Minimized)
            window.WindowState = (WindowState)state;
    }

    private async Task RestoreWindowForCurrentProjectAsync()
    {
        try
        {
            if (!_coordinator.Enabled || _mainWindow is null || !File.Exists(_sessionPath)) return;
            await using var input = File.OpenRead(_sessionPath);
            if (input.Length > 32 * 1024 * 1024) return;
            var snapshot = await JsonSerializer.DeserializeAsync(input, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot);
            if (snapshot is { Version: 1 } && string.Equals(snapshot.ProjectPath, _projects.CurrentProjectPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                ApplyWindowState(snapshot);
        }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Warn, "AutoSave", $"Restore window state failed: {ex.Message}"); }
    }

    private void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message)
    {
        if (_initialized) _ = RestoreWindowForCurrentProjectAsync();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_initialized && !_projects.IsBusy && !_projects.IsImageOperationRunning) await _coordinator.TickAsync();
    }

    private async Task SaveEditedDocumentsAsync()
    {
        if (!_initialized || _projects.IsBusy || _projects.IsImageOperationRunning) return;
        try
        {
            // The periodic save follows the same persistence boundary as the Save command:
            // documents, project files, page state, solution extension state and session layout.
            await _projects.AutoSaveAsync(persistProject: true, persistPageState: true);
            await SaveSessionAsync();
        }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "AutoSave", ex.Message); }
    }

    private async Task SaveSessionAsync()
    {
        if (!_initialized || _projects.IsBusy) return;
        try
        {
            // 轻量签名未变化时跳过整轮 DTO 构建与序列化（WriteIfChangedAsync 只省落盘，不省构建）。
            // Skip the full DTO build/serialize pass when the lightweight signature is unchanged
            // (WriteIfChangedAsync only avoids the disk write, not the build).
            var signature = ComputeSessionSignature();
            if (string.Equals(signature, _lastSessionSignature, StringComparison.Ordinal)) return;
            _lastSessionSignature = signature;

            var documents = WorkspaceSessionCapture.Documents(_workspace.Layout, Environment.CurrentDirectory);
            foreach (var document in documents)
                if (document.RelativePath.Length > 0) document.RelativePath = Path.GetFullPath(document.RelativePath);
            var snapshot = new WorkspaceSessionSnapshot
            {
                ProjectPath = _projects.CurrentProjectPath,
                Panels = WorkspacePanelService.Instance.CaptureVisibility(),
                Documents = documents,
                Docks = WorkspaceSessionCapture.Docks(_workspace.Layout)
            };
            if (_mainWindow is { } window)
            {
                snapshot.WindowWidth = window.Width is > 0 and < 10000 ? window.Width : null;
                snapshot.WindowHeight = window.Height is > 0 and < 10000 ? window.Height : null;
                snapshot.WindowX = window.Position.X;
                snapshot.WindowY = window.Position.Y;
                snapshot.WindowState = (int)(window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState);
            }
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
            if (_initialized && _coordinator.Enabled && _projects.CurrentProjectPath is { Length: > 0 } path)
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
        if (_initialized && _coordinator.Enabled)
        {
            if (_projects.CurrentProjectPath is { Length: > 0 } path)
                await _projects.PersistToAsync(path);
            await SaveSessionAsync();
        }
        return string.IsNullOrEmpty(_projects.CurrentProjectPath) || await _projects.CloseAfterSaveAsync();
    }

    /// <summary>Waits for existing work before prompting; never initiates a save before confirmation.</summary>
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
        WeakReferenceMessenger.Default.Unregister<ProjectContextChangedMessage>(this);
    }
}
