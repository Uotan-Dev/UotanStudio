using UotanStudio.Mcp;
using UotanStudio.Mcp.Http;

namespace UotanStudio.Services.Mcp;

/// <summary>
/// Owns the embedded MCP HTTP server: the expose toggle, port, sandbox root and flash gate start, re-target
/// or stop the loopback listener, which lives as long as the application.
/// <para>管理嵌入式 MCP HTTP 服务器：暴露开关、端口、沙箱根目录与刷写门控负责启动、换配置或停止回环监听，
/// 服务器随应用生命周期存在。</para>
/// </summary>
internal sealed class McpExposureService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpHttpServerHost? _host;

    /// <summary>Sandbox root live in <see cref="_host"/>; empty marks the unrestricted sandbox. <para>当前生效实例的沙箱根目录；空字符串表示不受限。</para></summary>
    private string _activeRoot = string.Empty;

    /// <summary>Whether the live instance registers the flashing tools. <para>当前生效实例是否注册刷写类工具。</para></summary>
    private bool _activeAllowFlash;

    private McpExposureService()
    {
    }

    /// <summary>The global singleton. <para>全局单例。</para></summary>
    /// <value>The shared exposure service instance. <para>共享的暴露服务实例。</para></value>
    internal static McpExposureService Instance { get; } = new();

    /// <summary>Gets whether the endpoint is listening. <para>获取当前是否正在监听。</para></summary>
    /// <value>True while the loopback listener is running. <para>回环监听正在运行时为 true。</para></value>
    internal bool IsRunning => _host?.IsRunning == true;

    /// <summary>Gets the listening port (0 while stopped). <para>获取当前监听端口（未运行时为 0）。</para></summary>
    /// <value>The active listening port, or 0 while stopped. <para>当前监听端口，停止时为 0。</para></value>
    internal int Port => _host?.Port ?? 0;

    /// <summary>Gets the reason the last start failed. <para>获取最近一次启动失败的原因。</para></summary>
    /// <value>The failure reason, or <see langword="null"/> while disabled or after a successful start.
    /// <para>失败原因，未启用或启动成功时为 <see langword="null"/>。</para></value>
    internal string? LastError { get; private set; }

    /// <summary>
    /// Applies the persisted exposure setting from the values already on disk, starting, re-targeting or stopping
    /// the listener with the stored sandbox root and flash gate; failures surface through <see cref="LastError"/>.
    /// <para>按磁盘上已持久化的值应用暴露设置，使用记录的沙箱根目录与刷写门控启动、换配置或停止监听；
    /// 失败通过 <see cref="LastError"/> 与日志上报。</para>
    /// </summary>
    /// <param name="enabled">Whether the server is exposed. <para>是否暴露为 MCP 服务器。</para></param>
    /// <param name="port">The requested listening port. <para>期望的监听端口。</para></param>
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
            rootDirectory = string.Empty;
            allowFlash = false;
        }

        Apply(enabled, port, rootDirectory, allowFlash);
    }

    /// <summary>
    /// Applies an explicit exposure configuration so the live server carries exactly the given port, sandbox root
    /// and flash gate; failures surface through <see cref="LastError"/> and the log.
    /// <para>应用显式的暴露配置，使运行中的服务器与给定的端口、沙箱根目录和刷写门控完全一致；
    /// 失败通过 <see cref="LastError"/> 与日志上报。</para>
    /// </summary>
    /// <param name="enabled">Whether the server is exposed. <para>是否暴露为 MCP 服务器。</para></param>
    /// <param name="port">The requested listening port. <para>期望的监听端口。</para></param>
    /// <param name="rootDirectory">Sandbox root; empty or whitespace means unrestricted. <para>沙箱根目录；空或仅空白表示不受限。</para></param>
    /// <param name="allowFlash">Whether the flashing, wiping and reboot tools are registered. <para>是否注册刷写、擦除、重启类工具。</para></param>
    internal void Apply(bool enabled, int port, string rootDirectory, bool allowFlash)
        => _ = ApplyAsync(enabled, port, rootDirectory, allowFlash);

    /// <summary>Stops the listener (used on application exit). <para>停止监听（用于应用退出）。</para></summary>
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
                new DesktopDeviceManagerGateway(), McpLauncher.CreateDesktopDeliveryTools(),
                () => new Delivery.DeliveryKeyProvider());
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
    /// Builds the sandbox for the configured root, falling back to the unrestricted sandbox with the flash gate
    /// closed when the stored path cannot be normalized; the fallback reason is logged.
    /// <para>按配置的根目录构建沙箱；持久化路径无法规范化时回退为不受限沙箱并关闭刷写门控，回退原因会写入日志。</para>
    /// </summary>
    /// <param name="rootDirectory">The persisted sandbox root. <para>持久化的沙箱根目录。</para></param>
    /// <param name="sandbox">The sandbox that will back the server. <para>最终生效的沙箱实例。</para></param>
    /// <param name="fallbackReason">The reason for falling back; null when the configured root was honoured. <para>回退原因；未回退时为 null。</para></param>
    /// <returns><see langword="true"/> when the configured root was used. <para>配置根目录被采用时为 true。</para></returns>
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