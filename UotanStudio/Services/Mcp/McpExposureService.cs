using UotanStudio.Mcp;
using UotanStudio.Mcp.Http;

namespace UotanStudio.Services.Mcp;

/// <summary>
/// Owns the embedded MCP HTTP server the settings page controls: the "expose as MCP server" toggle,
/// port, sandbox root and flash gate start, re-target or stop the loopback listener, and the server
/// keeps running for as long as the application lives.
/// <para>管理设置页控制的嵌入式 MCP HTTP 服务器：设置页的「暴露为 MCP 服务器」开关、端口、沙箱根目录与
/// 刷写门控负责启动、换配置或停止回环监听；服务器随应用生命周期存在。</para>
/// </summary>
internal sealed class McpExposureService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpHttpServerHost? _host;

    /// <summary><para>当前生效实例的沙箱根目录；空字符串表示不受限。</para>Sandbox root live in <see cref="_host"/>; empty marks the unrestricted sandbox.</summary>
    private string _activeRoot = string.Empty;

    /// <summary><para>当前生效实例是否注册刷写类工具。</para>Whether the live instance registers the flashing tools.</summary>
    private bool _activeAllowFlash;

    private McpExposureService()
    {
    }

    /// <summary><para>全局单例。</para>The global singleton.</summary>
    internal static McpExposureService Instance { get; } = new();

    /// <summary><para>获取当前是否正在监听。</para>Gets whether the endpoint is listening.</summary>
    internal bool IsRunning => _host?.IsRunning == true;

    /// <summary><para>获取当前监听端口（未运行时为 0）。</para>Gets the listening port (0 while stopped).</summary>
    internal int Port => _host?.Port ?? 0;

    /// <summary><para>获取最近一次启动失败的原因，成功或未启用时为 null。</para>
    /// Gets the reason the last start failed; null while disabled or after a successful start.</summary>
    internal string? LastError { get; private set; }

    /// <summary>
    /// Applies the persisted exposure setting from the values already on disk: starts, re-targets or
    /// stops the listener using the stored sandbox root and flash gate. Runs off the caller's thread
    /// and reports failures through <see cref="LastError"/> and the log, so a busy port never breaks a
    /// settings save.
    /// <para>按磁盘上已持久化的值应用暴露设置：使用记录的沙箱根目录与刷写门控启动、换配置或停止监听。
    /// 操作不占用调用方线程，失败通过 <see cref="LastError"/> 与日志上报，端口被占用不会中断设置保存。</para>
    /// </summary>
    /// <param name="enabled"><para>是否暴露为 MCP 服务器。</para>Whether the server is exposed.</param>
    /// <param name="port"><para>期望的监听端口。</para>The requested listening port.</param>
    internal void Apply(bool enabled, int port)
    {
        string rootDirectory;
        bool allowFlash;
        try
        {
            var settings = SettingsPersistenceService.Instance.LoadSettings();
            rootDirectory = settings.McpExposeRootDirectory;
            allowFlash = settings.McpExposeAllowFlash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Reading the file must never break startup; the defaults are the historical, unrestricted shape.
            rootDirectory = string.Empty;
            allowFlash = false;
        }

        Apply(enabled, port, rootDirectory, allowFlash);
    }

    /// <summary>
    /// Applies an explicit exposure configuration: starts, re-targets or stops the listener so the live
    /// server carries exactly the given port, sandbox root and flash gate. Runs off the caller's thread
    /// and reports failures through <see cref="LastError"/> and the log, so a busy port or an unusable
    /// root never breaks a settings save.
    /// <para>应用显式的暴露配置：启动、换配置或停止监听，使运行中的服务器与给定的端口、沙箱根目录和刷写门控
    /// 完全一致。操作不占用调用方线程，失败通过 <see cref="LastError"/> 与日志上报，端口被占用或根目录无效
    /// 都不会中断设置保存。</para>
    /// </summary>
    /// <param name="enabled"><para>是否暴露为 MCP 服务器。</para>Whether the server is exposed.</param>
    /// <param name="port"><para>期望的监听端口。</para>The requested listening port.</param>
    /// <param name="rootDirectory"><para>沙箱根目录；空或仅空白表示不受限。</para>Sandbox root; empty or whitespace means unrestricted.</param>
    /// <param name="allowFlash"><para>是否注册刷写、擦除、重启类工具。</para>Whether the flashing, wiping and reboot tools are registered.</param>
    internal void Apply(bool enabled, int port, string rootDirectory, bool allowFlash)
        => _ = ApplyAsync(enabled, port, rootDirectory, allowFlash);

    /// <summary><para>停止监听（用于应用退出）。</para>Stops the listener (used on application exit).</summary>
    internal void Stop() => _ = ApplyAsync(enabled: false, port: 0, rootDirectory: string.Empty, allowFlash: false);

    private async Task ApplyAsync(bool enabled, int port, string rootDirectory, bool allowFlash)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!enabled)
            {
                await StopCoreAsync("MCP exposure disabled.").ConfigureAwait(false);
                LastError = null;
                return;
            }

            var effectivePort = port is > 0 and <= 65535 ? port : AppSettings.DefaultMcpExposePort;
            if (!TryCreateSandbox(rootDirectory, out var sandbox, out var fallbackReason))
            {
                // The configured confinement could not be honoured, so the destructive gate stays closed.
                allowFlash = false;
            }

            var root = sandbox.Root ?? string.Empty;
            if (_host is { IsRunning: true } running && running.Port == effectivePort
                && string.Equals(_activeRoot, root, StringComparison.Ordinal)
                && _activeAllowFlash == allowFlash)
            {
                return;
            }

            await StopCoreAsync("MCP exposure restarting.").ConfigureAwait(false);

            var serverHost = new McpServerHost(sandbox, allowFlash,
                new DesktopDeviceManagerGateway(), McpLauncher.CreateDesktopDeliveryTools());
            var host = new McpHttpServerHost(serverHost.BuildTools(), serverHost.BuildResources());
            host.Start(effectivePort);

            _host = host;
            _activeRoot = root;
            _activeAllowFlash = allowFlash;
            LastError = null;
            var liveConfig = $"sandbox root: {(root.Length == 0 ? "unrestricted" : root)}"
                + (fallbackReason is null ? string.Empty : $"; configured root ignored: {fallbackReason}")
                + $"; flash tools: {(allowFlash ? "enabled" : "disabled")}";
            LogService.Instance.Write(LogLevel.Info, "McpExposure",
                $"MCP server listening on http://127.0.0.1:{host.Port}/ ({liveConfig})");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _host = null;
            _activeRoot = string.Empty;
            _activeAllowFlash = false;
            LogService.Instance.Write(LogLevel.Error, "McpExposure",
                $"Starting the MCP server on port {port} failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds the sandbox for the configured root, falling back to the unrestricted sandbox when the
    /// stored path cannot be normalized; an unusable root therefore never cancels the exposure, it only
    /// widens it back to the pre-setting behavior with the flash gate closed, and the reason is logged.
    /// <para>按配置的根目录构建沙箱；当持久化路径无法规范化时回退到不受限沙箱。无效的根目录不会取消暴露，
    /// 只是回到本设置出现前的行为并同时关闭刷写门控，回退原因会写入日志。</para>
    /// </summary>
    /// <param name="rootDirectory"><para>持久化的沙箱根目录。</para>The persisted sandbox root.</param>
    /// <param name="sandbox"><para>最终生效的沙箱实例。</para>The sandbox that will back the server.</param>
    /// <param name="fallbackReason"><para>回退原因；未回退时为 null。</para>The reason for falling back; null when the configured root was honoured.</param>
    /// <returns><para>配置根目录被采用时为 true。</para><see langword="true"/> when the configured root was used.</returns>
    private static bool TryCreateSandbox(string? rootDirectory, out PathSandbox sandbox, out string? fallbackReason)
    {
        var root = rootDirectory?.Trim() ?? string.Empty;
        if (root.Length == 0)
        {
            sandbox = PathSandbox.Unrestricted;
            fallbackReason = null;
            return true;
        }

        try
        {
            sandbox = new PathSandbox(root);
            fallbackReason = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                   or NotSupportedException or System.Security.SecurityException)
        {
            sandbox = PathSandbox.Unrestricted;
            fallbackReason = ex.Message;
            LogService.Instance.Write(LogLevel.Warn, "McpExposure",
                $"Ignoring MCP sandbox root \"{root}\": {ex.Message}");
            return false;
        }
    }

    private async Task StopCoreAsync(string reason)
    {
        if (_host is not { } host)
        {
            return;
        }

        _host = null;
        _activeRoot = string.Empty;
        _activeAllowFlash = false;
        var port = host.Port;
        await host.StopAsync().ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
        LogService.Instance.Write(LogLevel.Info, "McpExposure", $"MCP server on port {port} stopped ({reason})");
    }
}