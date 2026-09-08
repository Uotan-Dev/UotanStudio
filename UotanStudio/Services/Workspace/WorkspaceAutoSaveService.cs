using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
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
    private Task? _initialization;

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
    /// proportions/active items, node topology and zoom) so the timer skips full DTO rebuilds
    /// and serialization when nothing changed.
    /// </summary>
    private string ComputeSessionSignature()
    {
        var sb = new System.Text.StringBuilder(64);
        var documents = WorkspaceSessionCapture.Documents(_workspace.Layout, Environment.CurrentDirectory);
        sb.Append(documents.Count).Append('|');
        foreach (var document in documents)
            sb.Append(document.Kind).Append(':').Append(document.RelativePath).Append(':')
              .Append(document.IsActive ? 'a' : '-').Append(';');
        foreach (var dock in WorkspaceSessionCapture.Docks(_workspace.Layout))
            sb.Append(dock.Id).Append(':').Append(dock.Proportion?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "-")
              .Append(':').Append(dock.ActiveId ?? "-").Append(';');
        if (_projects.NodeEditorVM is { } nodes)
        {
            sb.Append('|').Append(nodes.Nodes.Count).Append(':').Append(nodes.Connections.Count)
                .Append(':').Append(nodes.Zoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(nodes.ShowMinimap ? '1' : '0');
        }
        return sb.ToString();
    }

    internal Task InitializeAsync() => _initialization ??= RestoreAsync();

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
                        if (state.Proportion is > 0 and <= 1) dock.Proportion = state.Proportion.Value;
                        if (!string.IsNullOrEmpty(state.ActiveId)
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
            _timer.Start();
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.AutoSave))
            _coordinator.Enabled = _settings.AutoSave;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_initialized && !_projects.IsBusy) await _coordinator.TickAsync();
    }

    private async Task SaveEditedDocumentsAsync()
    {
        if (!_initialized || _projects.IsBusy) return;
        try { await _projects.AutoSaveAsync(persistProject: false); }
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

    internal async Task FlushAsync()
    {
        if (!_initialized) return;
        await _projects.WaitForIdleAsync();
        await _coordinator.FlushAsync();
        if (_coordinator.Enabled) await _projects.AutoSaveAsync(persistPageState: false);
    }

    internal async Task SaveOnCloseAsync()
    {
        _timer.Stop();
        if (!_initialized) return;
        await _projects.WaitForIdleAsync();
        await _coordinator.FlushAsync();
        if (_coordinator.Enabled)
        {
            await _projects.AutoSaveAsync();
            await SaveSessionAsync();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _settings.PropertyChanged -= OnSettingsChanged;
        _projects.BeforeProjectChangeAsync = null;
    }
}
