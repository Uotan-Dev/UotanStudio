using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Porta.Pty;
using UotanStudio.Build;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Terminal;
using UotanStudio.Services.Terminal.Wrapped;

namespace UotanStudio.ViewModels.Workspace;

internal partial class TerminalViewModel : Tool, IDisposable
{
    private bool _initialized;
    private bool _disposed;
    private readonly int _number;
    private readonly Action<TerminalViewModel> _newTerminal;
    private readonly Action<TerminalViewModel> _closeTerminal;
    private readonly DetectedDevice? _deviceShell;
    public IReadOnlyList<TerminalProfile> Profiles { get; }
    [ObservableProperty] private TerminalProfile? _selectedProfile;
    [ObservableProperty] private TerminalSessionViewModel? _session;
    [ObservableProperty] private string? _errorMessage;

    internal TerminalViewModel(int number, TerminalProfile? profile,
        Action<TerminalViewModel> newTerminal, Action<TerminalViewModel> closeTerminal,
        DetectedDevice? deviceShell = null)
    {
        _number = number;
        _newTerminal = newTerminal;
        _closeTerminal = closeTerminal;
        _deviceShell = deviceShell;
        var standard = TerminalShellService.GetProfiles();
        Profiles = profile is { Name: "Device shell" } ? [profile, .. standard] : standard;
        SelectedProfile = profile is { Name: "Device shell" } ? profile
            : Profiles.FirstOrDefault(candidate => string.Equals(candidate.Executable, profile?.Executable,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                ?? Profiles.FirstOrDefault();
        Id = $"Terminal.{number}";
        CanClose = true;
        OnLanguageChanged(this, EventArgs.Empty);
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    internal void Initialize()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        if (SelectedProfile is not { } profile)
        {
            ErrorMessage = LanguageService.Instance.GetString("Terminal_NoShell");
            return;
        }
        ErrorMessage = null;
        var projectDirectory = ProjectContextService.Instance.RootDirectory;
        var directory = Directory.Exists(projectDirectory) ? projectDirectory!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(directory)) directory = Environment.CurrentDirectory;
        Session = _deviceShell is { } device
            ? new TerminalSessionViewModel(profile, directory, device)
            : new TerminalSessionViewModel(profile, directory);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        var title = LanguageService.Instance.GetString("Dock_Terminal");
        Title = _number == 1 ? title : $"{title} {_number}";
        foreach (var profile in Profiles)
        {
            profile.RefreshDisplayName();
        }
        Session?.RefreshStatus();
    }

    /// <summary>
    /// Gets the profile a new terminal is opened with: a device shell terminal, or a toolbar profile
    /// that carries no arguments, falls back to the default shell instead of spawning the host again.
    /// <para>获取新终端使用的配置：设备 shell 终端、或工具栏中不带参数的那个配置，都回退到默认 shell，
    /// 而不会再次启动宿主本身。</para>
    /// </summary>
    internal TerminalProfile? CloneProfile =>
        _deviceShell is null && SelectedProfile is { Arguments.Length: > 0 } selected ? selected : null;

    [RelayCommand]
    private void NewTerminal()
    {
        if (!_disposed) _newTerminal(this);
    }

    [RelayCommand]
    private void CloseTerminal()
    {
        if (!_disposed) _closeTerminal(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        var session = Session;
        Session = null;
        session?.Dispose();
    }
}

internal partial class TerminalSessionViewModel(
    TerminalProfile profile, string directory, DetectedDevice? deviceShell = null) : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _started;
    private bool _disposed;
    private int? _exitCode;
    private string? _failure;
    public TerminalProfile Profile { get; } = profile;
    public string StartingDirectory { get; } = directory;
    internal IPtyConnection? Connection { get; private set; }

    /// <summary>
    /// Gets whether this session is the in-process device shell, which never takes command
    /// interception. <para>获取本会话是否为进程内设备 shell；设备 shell 不参与命令拦截。</para></summary>
    internal bool IsDeviceShell => deviceShell is not null;

    /// <summary>
    /// Gets the wrapped command runner for local shells, or <c>null</c> for the device shell. The
    /// runner routes intercepted adb/fastboot/hdc commands through the host's own device monitor and
    /// opens the device shell panel for interactive shell requests.
    /// <para>获取本地 shell 的包装命令执行器；设备 shell 为 <c>null</c>。执行器把被拦截的 adb/fastboot/hdc
    /// 命令路由到宿主自己的设备监视器，交互式 shell 请求则打开设备 shell 面板。</para></summary>
    internal WrappedCommandRunner? Runner { get; } = deviceShell is null ? CreateRunner() : null;

    private static WrappedCommandRunner CreateRunner()
    {
        var monitor = App.DeviceMonitor;
        var gateway = new DeviceMonitorCommandGateway(monitor, device => OpenShellPanelAsync(monitor, device));
        return new WrappedCommandRunner(gateway, BuildInfo.Version);
    }

    private static async Task OpenShellPanelAsync(DeviceMonitorService monitor, DetectedDevice device) =>
        await Dispatcher.UIThread.InvokeAsync(() => DeviceShellService.OpenInteractiveShell(monitor, device))
            .GetTask().ConfigureAwait(false);
    [ObservableProperty] private string _workingDirectory = directory;
    [ObservableProperty] private bool _isStarting;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _status;

    internal async Task StartAsync(int columns, int rows)
    {
        if (_started || _disposed) return;
        _started = true;
        IsStarting = true;
        RefreshStatus();
        var token = _lifetime.Token;
        try
        {
            IPtyConnection? connection = deviceShell is { } shell
                ? DeviceShellService.CreateConnection(App.DeviceMonitor, shell)
                : await Task.Run(() => PtyProvider.SpawnAsync(new PtyOptions
                {
                    Name = "UotanStudio",
                    App = Profile.Executable,
                    CommandLine = Profile.Arguments,
                    Cwd = StartingDirectory,
                    Cols = Math.Max(2, columns),
                    Rows = Math.Max(1, rows),
                    Environment = new Dictionary<string, string>
                    {
                        ["TERM"] = "xterm-256color",
                        ["COLORTERM"] = "truecolor",
                        ["TERM_PROGRAM"] = "UotanStudio"
                    }
                }, token), token);
            if (_disposed)
            {
                connection.Dispose();
                return;
            }
            Connection = connection;
            if (connection is DeviceShellConnection shellConnection)
            {
                shellConnection.Exited += OnShellExited;
            }
            else
            {
                connection.ProcessExited += OnProcessExited;
            }

            IsRunning = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _failure = UserError.Describe(ex);
                LogService.Instance.Write(LogLevel.Error, "Terminal", ex.Message);
            }
        }
        finally
        {
            IsStarting = false;
            RefreshStatus();
            if (_disposed) _lifetime.Dispose();
        }
    }

    internal void ReportExit(int code)
    {
        if (_disposed) return;
        _exitCode = code;
        IsRunning = false;
        RefreshStatus();
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e) =>
        Dispatcher.UIThread.Post(() => ReportExit(e.ExitCode));

    private void OnShellExited(object? sender, int exitCode) =>
        Dispatcher.UIThread.Post(() => ReportExit(exitCode));

    internal void RefreshStatus()
    {
        var language = LanguageService.Instance;
        Status = _failure is not null ? language.GetString("Terminal_StartFailed", _failure)
            : _exitCode is { } code ? language.GetString("Terminal_Exited", code)
            : IsStarting ? language.GetString("Terminal_Starting") : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (Connection is { } connection)
        {
            if (connection is DeviceShellConnection shellConnection)
            {
                shellConnection.Exited -= OnShellExited;
            }
            else
            {
                connection.ProcessExited -= OnProcessExited;
            }

            connection.Dispose();
            Connection = null;
        }
        if (!IsStarting) _lifetime.Dispose();
        IsRunning = false;
    }
}