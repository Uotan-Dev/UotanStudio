using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Core;
using UotanStudio.Messages;
using UotanStudio.Projects;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;

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
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);
    }

    /// <summary>
    /// Lightweight signature of the last session snapshot; the whole DTO build/serialize
    /// pass is skipped when nothing changed.
    /// <para>上次会话快照的轻量签名；无变化时跳过整轮 DTO 构建与序列化。</para>
    /// </summary>
    private string? _lastSessionSignature;

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
            var signature = ComputeSessionSignature();
            if (string.Equals(signature, _lastSessionSignature, StringComparison.Ordinal)) return;

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
        WeakReferenceMessenger.Default.Unregister<ProjectContextChangedMessage>(this);
    }
}