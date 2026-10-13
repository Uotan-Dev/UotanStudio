using UotanStudio.Mcp;

namespace UotanStudio.Services.Mcp;

/// <summary>
/// Owns the MCP client configuration: pushing a server list replaces the active configs and drops cached
/// connections, and failures are reported through the log only.
/// <para>管理 MCP 客户端配置：推送服务器列表会替换生效配置并清空缓存连接，失败只记录日志。</para>
/// </summary>
internal sealed class McpClientConnectionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private McpClientConnectionService()
    {
    }

    /// <summary>The global singleton. <para>全局单例。</para></summary>
    /// <value>The shared connection service instance. <para>共享的连接服务实例。</para></value>
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
            LogService.Instance.Write(LogLevel.Error, "McpClient", $"Loading MCP client servers failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the given client configs: each entry is converted into the client DTO and pushed to
    /// <see cref="McpClientService"/>, which drops cached connections. Runs off the current thread and logs failures.
    /// <para>应用给定的客户端配置：逐条转换为客户端 DTO 并推送给 <see cref="McpClientService"/>，由其释放
    /// 缓存连接。操作在当前线程之外执行，失败只记录日志。</para>
    /// </summary>
    /// <param name="servers">The server configs to activate. <para>要生效的服务器配置列表。</para></param>
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