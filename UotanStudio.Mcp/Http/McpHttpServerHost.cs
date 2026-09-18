using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UotanStudio.Mcp.Http;

/// <summary>
/// Serves the UotanStudio MCP tools over the Streamable HTTP transport on a loopback port, so
/// external clients connect to <c>http://127.0.0.1:&lt;port&gt;/</c> instead of spawning a stdio
/// process. Started by the desktop host when the settings page enables MCP exposure.
/// <para>把 UotanStudio 的 MCP 工具以 Streamable HTTP 传输暴露在回环端口上：外部客户端直接连接
/// <c>http://127.0.0.1:&lt;port&gt;/</c>，无需自行启动 stdio 进程。由桌面宿主在设置页开启 MCP 暴露时启动。</para>
/// Only the loopback interface is bound, requests carrying a non-loopback <c>Origin</c> are rejected
/// (DNS-rebinding guard), and every accepted path maps to the single MCP endpoint.
/// <para>只绑定回环接口；携带非回环 <c>Origin</c> 的请求会被拒绝（防 DNS 重绑定）；任意路径都映射到同一个
/// MCP 端点。</para>
/// </summary>
public sealed class McpHttpServerHost : IAsyncDisposable
{
    private const int MaxRequestHeadBytes = 16 * 1024;
    private const int MaxRequestBodyBytes = 16 * 1024 * 1024;

    /// <summary>Idle keep-alive window; a connection without a new request is closed after this.
    /// <para>空闲保活窗口；超过该时间没有新请求的连接会被关闭。</para></summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Source-generated metadata keeps request deserialization trimming/AOT safe.
    /// <para>使用源生成的元数据，使请求反序列化对裁剪/AOT 安全。</para></summary>
    private static readonly JsonTypeInfo<JsonRpcMessage> MessageTypeInfo =
        (JsonTypeInfo<JsonRpcMessage>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));

    private readonly IReadOnlyList<McpServerTool> _tools;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly object _sync = new();

    private Session? _session;
    private bool _disposed;

    /// <summary>
    /// Creates the host from an already built tool list (see <see cref="McpServerHost.BuildTools"/>).
    /// <para>由已构建的工具列表创建宿主（见 <see cref="McpServerHost.BuildTools"/>）。</para>
    /// </summary>
    /// <param name="tools">Tools every session registers. <para>会话注册的工具。</para></param>
    /// <param name="loggerFactory">Optional SDK diagnostics logger factory. <para>可选的 SDK 诊断日志工厂。</para></param>
    public McpHttpServerHost(IReadOnlyList<McpServerTool> tools, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger("UotanStudio.Mcp");
    }

    /// <summary>Gets a value indicating whether the port is currently served.
    /// <para>获取当前是否正在监听。</para></summary>
    public bool IsRunning { get; private set; }

    /// <summary>Gets the bound loopback port (resolved value when port 0 was requested).
    /// <para>获取已绑定的回环端口（请求 0 时为系统分配的实际端口）。</para></summary>
    public int Port { get; private set; }

    /// <summary>
    /// Binds the loopback port and starts serving MCP requests.
    /// <para>绑定回环端口并开始处理 MCP 请求。</para>
    /// </summary>
    /// <param name="port">Loopback TCP port; 0 lets the OS pick a free one. <para>回环 TCP 端口；0 表示由系统分配。</para></param>
    /// <exception cref="SocketException">The port is unavailable. <para>端口不可用时抛出。</para></exception>
    public void Start(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                throw new InvalidOperationException("The MCP HTTP server is already running.");
            }

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var cancellation = new CancellationTokenSource();
            var transport = new StreamableHttpServerTransport(_loggerFactory);
            var session = new Session(listener, transport, McpServer.Create(transport, McpServerHost.CreateOptions(_tools), _loggerFactory), cancellation);

            _session = session;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            IsRunning = true;

            session.AcceptLoop = AcceptLoopAsync(session);
            session.ServerLoop = RunSessionAsync(session);
        }
    }

    /// <summary>
    /// Stops serving, releases the port and waits for the session to unwind.
    /// <para>停止服务、释放端口并等待会话收敛。</para>
    /// </summary>
    public async Task StopAsync()
    {
        Session? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
            IsRunning = false;
        }

        if (session is null)
        {
            return;
        }

        session.Cancellation.Cancel();
        session.Listener.Stop();
        foreach (var connection in _connections.Keys)
        {
            connection.Dispose();
        }

        _connections.Clear();

        await IgnoreAsync(session.AcceptLoop).ConfigureAwait(false);
        await IgnoreAsync(session.ServerLoop).ConfigureAwait(false);
        await DisposeQuietlyAsync(session.Server).ConfigureAwait(false);
        await DisposeQuietlyAsync(session.Transport).ConfigureAwait(false);
        session.Cancellation.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _session?.Cancellation.Dispose();
    }

    private async Task AcceptLoopAsync(Session session)
    {
        var cancellationToken = session.Cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await session.Listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _connections.TryAdd(client, 0);
            _ = ServeConnectionAsync(session, client);
        }
    }

    private async Task RunSessionAsync(Session session)
    {
        try
        {
            await session.Server.RunAsync(session.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MCP HTTP session ended with an error.");
        }
    }

    /// <summary>
    /// Serves one client connection: keeps handling requests while the client keeps the connection
    /// alive and the protocol allows it.
    /// <para>处理单个客户端连接：只要客户端保持连接且协议允许，就持续处理请求。</para>
    /// </summary>
    private async Task ServeConnectionAsync(Session session, TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            while (!session.Cancellation.IsCancellationRequested)
            {
                HttpRequest? request;
                try
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token);
                    idle.CancelAfter(IdleTimeout);
                    request = await HttpRequest.ReadAsync(stream, MaxRequestHeadBytes, MaxRequestBodyBytes, idle.Token).ConfigureAwait(false);
                }
                catch (HttpProtocolException ex)
                {
                    await IgnoreAsync(HttpResponseWriter.WriteTextAsync(stream, ex.StatusCode, ex.Reason, ex.Message, keepAlive: false, CancellationToken.None)).ConfigureAwait(false);
                    return;
                }
                catch (Exception)
                {
                    // Idle timeout, server shutdown or a client that vanished mid-request.
                    return;
                }

                if (request is null)
                {
                    return;
                }

                if (!await HandleRequestAsync(session, request, stream).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MCP HTTP connection ended with an error.");
        }
        finally
        {
            _connections.TryRemove(client, out _);
            client.Dispose();
        }
    }

    /// <summary>Returns false when the connection must close after this request.
    /// <para>返回 false 表示处理完该请求后必须关闭连接。</para></summary>
    private async Task<bool> HandleRequestAsync(Session session, HttpRequest request, Stream stream)
    {
        var cancellationToken = session.Cancellation.Token;

        if (!IsOriginAllowed(request.Origin))
        {
            await IgnoreAsync(HttpResponseWriter.WriteTextAsync(stream, 403, "Forbidden", "Cross-origin requests are not accepted.", keepAlive: false, cancellationToken)).ConfigureAwait(false);
            return false;
        }

        switch (request.Method)
        {
            case "POST":
                return await HandlePostAsync(session, request, stream, cancellationToken).ConfigureAwait(false);

            case "GET":
                await HandleGetAsync(session, stream, cancellationToken).ConfigureAwait(false);
                return false;

            case "DELETE":
                // The exposed server keeps a single process-wide session, so termination is a no-op.
                await IgnoreAsync(HttpResponseWriter.WriteEmptyAsync(stream, 204, "No Content", request.KeepAlive, cancellationToken)).ConfigureAwait(false);
                return request.KeepAlive;

            default:
                await IgnoreAsync(HttpResponseWriter.WriteTextAsync(stream, 405, "Method Not Allowed", "Supported methods: POST, GET, DELETE.", request.KeepAlive, cancellationToken, "Allow: POST, GET, DELETE\r\n")).ConfigureAwait(false);
                return request.KeepAlive;
        }
    }

    private async Task<bool> HandlePostAsync(Session session, HttpRequest request, Stream stream, CancellationToken cancellationToken)
    {
        JsonRpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(request.Body, MessageTypeInfo);
        }
        catch (JsonException)
        {
            message = null;
        }

        if (message is null)
        {
            await IgnoreAsync(HttpResponseWriter.WriteJsonAsync(
                stream,
                400,
                "Bad Request",
                """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error."}}""",
                keepAlive: false,
                cancellationToken)).ConfigureAwait(false);
            return false;
        }

        if (message is not JsonRpcRequest)
        {
            // Notifications and client responses carry no reply; acknowledge them per the spec.
            await IgnoreAsync(HttpResponseWriter.WriteEmptyAsync(stream, 202, "Accepted", request.KeepAlive, cancellationToken)).ConfigureAwait(false);
            return request.KeepAlive;
        }

        await HttpResponseWriter.WriteStreamHeadAsync(stream, 200, "OK", "text/event-stream", request.KeepAlive, cancellationToken).ConfigureAwait(false);

        var body = new ChunkedBodyStream(stream);
        try
        {
            await session.Transport.HandlePostRequestAsync(message, body, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await IgnoreAsync(body.CompleteAsync(cancellationToken)).ConfigureAwait(false);
        }

        return request.KeepAlive;
    }

    private async Task HandleGetAsync(Session session, Stream stream, CancellationToken cancellationToken)
    {
        await HttpResponseWriter.WriteStreamHeadAsync(stream, 200, "OK", "text/event-stream", keepAlive: false, cancellationToken).ConfigureAwait(false);

        var body = new ChunkedBodyStream(stream);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = MonitorDisconnectAsync(stream, linked);

        try
        {
            await session.Transport.HandleGetRequestAsync(body, linked.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Client disconnected or the transport is shutting down.
        }
        finally
        {
            await IgnoreAsync(body.CompleteAsync(CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels the standalone SSE stream once the client closes the connection: HTTP clients never
    /// send data on that stream, so reading it to EOF is the disconnect signal.
    /// <para>客户端关闭连接后取消独立 SSE 流：HTTP 客户端不会在该流上发送数据，因此读到 EOF 即断开。</para>
    /// </summary>
    private static async Task MonitorDisconnectAsync(Stream stream, CancellationTokenSource linked)
    {
        var buffer = new byte[1];
        try
        {
            while (await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception)
        {
        }

        try
        {
            linked.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsOriginAllowed(string? origin)
        => string.IsNullOrEmpty(origin)
            || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback);

    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>One running MCP session: the listener, its transport, the server and its loops.
    /// <para>一个运行中的 MCP 会话：监听器、传输、服务器及其循环任务。</para>
    /// </summary>
    private sealed class Session(
        TcpListener listener,
        StreamableHttpServerTransport transport,
        McpServer server,
        CancellationTokenSource cancellation)
    {
        internal TcpListener Listener { get; } = listener;

        internal StreamableHttpServerTransport Transport { get; } = transport;

        internal McpServer Server { get; } = server;

        internal CancellationTokenSource Cancellation { get; } = cancellation;

        internal Task AcceptLoop { get; set; } = Task.CompletedTask;

        internal Task ServerLoop { get; set; } = Task.CompletedTask;
    }
}