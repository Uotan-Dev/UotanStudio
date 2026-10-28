using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Dock.Model.Core;
using UotanStudio.Projects;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Shell;
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
        if (resetPlacement && _mainWindow is { } window)
        {
            _ = ShowPlacementResetNoticeAsync(window);
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
            SaveWindowPlacement();
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