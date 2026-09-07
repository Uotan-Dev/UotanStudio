using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Services.Mcp;

/// <summary>
/// <para>MCP 服务器宿主：把本应用的固件能力暴露为 MCP 服务器（TCP 流传输），
/// 供外部 AI 助手/客户端按 MCP 协议调用；由设置页的「暴露为 MCP 服务器」开关与端口控制。</para>
/// MCP server host: exposes this application's firmware capabilities as an MCP server over a TCP
/// stream transport, callable by external AI assistants/clients per the MCP protocol; controlled by
/// the settings page's "expose as MCP server" toggle and port.
/// </summary>
public sealed class McpHostService
{
    /// <summary><para>全局单例。</para>The global singleton.</summary>
    public static McpHostService Instance { get; } = new();

    private readonly Lock _sync = new();
    private ServiceProvider? _provider;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _isRunning;

    private McpHostService()
    {
    }

    /// <summary><para>是否正在监听。</para>Whether the host is listening.</summary>
    public bool IsRunning
    {
        get { lock (_sync) return _isRunning; }
        private set => _isRunning = value;
    }

    /// <summary>
    /// <para>在指定端口启动 MCP 服务器（先停止已有实例）；工具经 DI 发现
    /// （<see cref="UotanStudioMcpTools"/>，见 <c>AddMcpServer</c>）。</para>
    /// Starts the MCP server on the given port (stopping any existing instance first); tools are
    /// discovered via DI (<see cref="UotanStudioMcpTools"/>, see <c>AddMcpServer</c>).
    /// </summary>
    /// <param name="port">监听端口。<para>Listening port.</para></param>
    public void Start(int port)
    {
        lock (_sync)
        {
            Stop();

            var services = new ServiceCollection();
            services.AddMcpServer(_ => { });
            services.AddSingleton<UotanStudioMcpTools>();
            _provider = services.BuildServiceProvider();

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_provider, _listener, _cts.Token);
            IsRunning = true;
            LogService.Instance.Write(LogLevel.Info, "McpHost", $"MCP server listening on 127.0.0.1:{port}");
        }
    }

    /// <summary>
    /// <para>停止 MCP 服务器并释放资源。</para>
    /// Stops the MCP server and releases resources.
    /// </summary>
    public void Stop()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // 忽略停止期间的监听异常。
                // Ignore listener exceptions during shutdown.
            }

            _cts?.Dispose();
            _provider?.Dispose();
            _provider = null;
            _listener = null;
            _cts = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// <para>接受连接循环：每个连接为单个会话，用 <see cref="StreamServerTransport"/> 承载。</para>
    /// Accept loop: each connection hosts a single session over a
    /// <see cref="StreamServerTransport"/>.
    /// </summary>
    private static async Task AcceptLoopAsync(ServiceProvider provider, TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => ServeConnectionAsync(provider, tcp, ct), CancellationToken.None);
        }
    }

    /// <summary>
    /// <para>承载单个客户端连接：从 DI 取 <see cref="McpServer"/> 并以流传输运行。</para>
    /// Hosts a single client connection: obtains the <see cref="McpServer"/> from DI and runs it over
    /// the stream transport.
    /// </summary>
    private static async Task ServeConnectionAsync(ServiceProvider provider, TcpClient tcp, CancellationToken ct)
    {
        using var _ = tcp;
        try
        {
            var stream = tcp.GetStream();
            var transport = new StreamServerTransport(stream, stream);
            var server = McpServer.Create(transport, new McpServerOptions(), loggerFactory: null, serviceProvider: provider);
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "McpHost", ex.Message);
        }
    }
}