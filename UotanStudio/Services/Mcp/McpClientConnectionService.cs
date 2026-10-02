using UotanStudio.Mcp;

namespace UotanStudio.Services.Mcp;

/// <summary>
/// Owns the MCP client configuration the settings page maintains: pushing a server list to
/// <see cref="McpClientService"/> replaces the active configs and drops cached connections, so a
/// saved settings change takes effect immediately for later tool listing and calls. Failures are
/// reported through the log and never break a settings save or application startup.
/// <para>管理设置页维护的 MCP 客户端配置：把服务器列表推送给 <see cref="McpClientService"/> 会替换生效
/// 配置并清空缓存连接，使保存后的设置对后续的工具列举与调用立即生效。失败只记录日志，
/// 不会中断设置保存或应用启动。</para>
/// </summary>
internal sealed class McpClientConnectionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private McpClientConnectionService()
    {
    }

    /// <summary><para>全局单例。</para>The global singleton.</summary>
    internal static McpClientConnectionService Instance { get; } = new();

    /// <summary>
    /// Applies the persisted client configs from the values already on disk, mirroring how
    /// <see cref="McpExposureService"/> applies exposure settings at startup.
    /// <para>按磁盘上已持久化的值应用客户端配置，与 <see cref="McpExposureService"/>
    /// 启动时应用暴露设置的方式对应。</para>
    /// </summary>
    internal void Apply()
    {
        try
        {
            var settings = SettingsPersistenceService.Instance.LoadSettings();
            Apply(settings.McpServers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Reading the file must never break startup; the client simply keeps no servers configured.
            LogService.Instance.Write(LogLevel.Error, "McpClient", $"Loading MCP client servers failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the given client configs: converts each host config into the client DTO and pushes the
    /// list to <see cref="McpClientService"/>, which disposes cached connections so the new list is
    /// authoritative. Runs off the caller's thread; failures are logged only.
    /// <para>应用给定的客户端配置：把每个宿主配置转换为客户端 DTO 并推送给
    /// <see cref="McpClientService"/>，由其释放缓存连接使新列表成为权威配置。
    /// 操作不占用调用方线程；失败只记录日志。</para>
    /// </summary>
    /// <param name="servers"><para>要生效的服务器配置列表。</para>The server configs to activate.</param>
    internal void Apply(IReadOnlyList<McpServerConfig> servers)
        => _ = ApplyAsync(servers);

    private async Task ApplyAsync(IReadOnlyList<McpServerConfig> servers)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var configs = new List<McpClientServerConfig>(servers.Count);
            foreach (var server in servers)
            {
                configs.Add(new McpClientServerConfig
                {
                    Name = server.Name,
                    Transport = server.Transport switch
                    {
                        McpTransportKind.Stdio => global::UotanStudio.Mcp.McpTransportKind.Stdio,
                        _ => global::UotanStudio.Mcp.McpTransportKind.Http
                    },
                    Command = server.Command,
                    Arguments = server.Arguments,
                    Url = server.Url,
                    Enabled = server.Enabled
                });
            }

            await McpClientService.Instance.SetServersAsync(configs).ConfigureAwait(false);
            LogService.Instance.Write(LogLevel.Info, "McpClient",
                $"MCP client servers applied: {configs.Count} configured.");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "McpClient",
                $"Applying MCP client servers failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}