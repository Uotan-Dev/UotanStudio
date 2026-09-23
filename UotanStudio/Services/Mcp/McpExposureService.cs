using UotanStudio.Mcp;
using UotanStudio.Mcp.Http;

namespace UotanStudio.Services.Mcp;

/// <summary>
/// Owns the embedded MCP HTTP server the settings page controls: the "expose as MCP server" toggle
/// and port start, re-target or stop the loopback listener, and the server keeps running for as long
/// as the application lives.
/// <para>管理设置页控制的嵌入式 MCP HTTP 服务器：设置页的「暴露为 MCP 服务器」开关与端口负责启动、
/// 换端口或停止回环监听；服务器随应用生命周期存在。</para>
/// </summary>
internal sealed class McpExposureService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpHttpServerHost? _host;

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
    /// Applies the persisted exposure setting: starts, re-targets or stops the listener. Runs off the
    /// caller's thread and reports failures through <see cref="LastError"/> and the log, so a busy
    /// port never breaks a settings save.
    /// <para>应用持久化的暴露设置：启动、换端口或停止监听。操作不占用调用方线程，失败通过
    /// <see cref="LastError"/> 与日志上报，端口被占用不会中断设置保存。</para>
    /// </summary>
    internal void Apply(bool enabled, int port) => _ = ApplyAsync(enabled, port);

    /// <summary><para>停止监听（用于应用退出）。</para>Stops the listener (used on application exit).</summary>
    internal void Stop() => _ = ApplyAsync(enabled: false, port: 0);

    private async Task ApplyAsync(bool enabled, int port)
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
            if (_host is { IsRunning: true } running && running.Port == effectivePort)
            {
                return;
            }

            await StopCoreAsync("MCP exposure restarting.").ConfigureAwait(false);

            McpLauncher.EnableDeliveryBuild();
            var tools = new McpServerHost(PathSandbox.Unrestricted, allowFlash: false,
                new DesktopDeviceManagerGateway()).BuildTools();
            var host = new McpHttpServerHost(tools);
            host.Start(effectivePort);

            _host = host;
            LastError = null;
            LogService.Instance.Write(LogLevel.Info, "McpExposure",
                $"MCP server listening on http://127.0.0.1:{host.Port}/");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _host = null;
            LogService.Instance.Write(LogLevel.Error, "McpExposure",
                $"Starting the MCP server on port {port} failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(string reason)
    {
        if (_host is not { } host)
        {
            return;
        }

        _host = null;
        var port = host.Port;
        await host.StopAsync().ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
        LogService.Instance.Write(LogLevel.Info, "McpExposure", $"MCP server on port {port} stopped ({reason})");
    }
}