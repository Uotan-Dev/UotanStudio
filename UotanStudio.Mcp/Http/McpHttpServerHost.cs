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
/// Serves the UotanStudio MCP tools and resources over the Streamable HTTP transport on a loopback port, so
/// external clients connect to <c>http://127.0.0.1:&lt;port&gt;/</c> instead of spawning a stdio
/// process. Started by the desktop host when the settings page enables MCP exposure.
/// <para>把 UotanStudio 的 MCP 工具与资源以 Streamable HTTP 传输暴露在回环端口上：外部客户端直接连接
/// <c>http://127.0.0.1:&lt;port&gt;/</c>，无需自行启动 stdio 进程。由桌面宿主在设置页开启 MCP 暴露时启动。</para>
/// Only the loopback interface is bound, requests carrying a non-loopback <c>Origin</c> are rejected
/// (DNS-rebinding guard), and every accepted path maps to the single MCP endpoint.
/// <para>只绑定回环接口；携带非回环 <c>Origin</c> 的请求会被拒绝（防 DNS 重绑定）；任意路径都映射到同一个
/// MCP 端点。</para>
/// Every JSON-RPC request body is handed to the transport as-is: the host never filters by method name,
/// so <c>tools/*</c>, <c>resources/*</c> and any other method the <see cref="McpServer"/> registers are
/// reachable through this listener. Which methods actually resolve is decided by the shared
/// <c>McpServerHost.CreateOptions</c> call, because the SDK registers a method group only when the
/// matching primitive collection is supplied.
/// <para>每个 JSON-RPC 请求体都会原样交给传输层：宿主从不按方法名过滤，因此
/// <c>tools/*</c>、<c>resources/*</c> 以及 <see cref="McpServer"/> 注册的其他方法都能经由该监听器到达。
/// 哪些方法真正可解析取决于共用的 <c>McpServerHost.CreateOptions</c> 调用，
/// 因为 SDK 只在给出对应原语集合时才注册该方法组。</para>
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
    private readonly IReadOnlyList<McpServerResource> _resources;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly Lock _sync = new();

    private Session? _session;
    private bool _disposed;

    /// <summary>
    /// Creates the host from an already built tool list (see <see cref="McpServerHost.BuildTools"/>),
    /// optionally adding the resources built by <see cref="McpServerHost.BuildResources"/>.
    /// <para>由已构建的工具列表（见 <see cref="McpServerHost.BuildTools"/>）创建宿主，并可选加入
    /// <see cref="McpServerHost.BuildResources"/> 构建的资源。</para>
    /// The Streamable HTTP listener forwards every JSON-RPC method to the server unchanged, so omitting
    /// the resources is what keeps <c>resources/list</c>, <c>resources/templates/list</c> and
    /// <c>resources/read</c> answering <c>-32601</c>: the SDK registers those method groups only when a
    /// resource collection is present in the shared options.
    /// <para>Streamable HTTP 监听器会把任意 JSON-RPC 方法原样转发给服务器，因此未给出资源才是
    /// <c>resources/list</c>、<c>resources/templates/list</c> 与 <c>resources/read</c> 仍返回
    /// <c>-32601</c> 的原因：SDK 只在共用选项中存在资源集合时才注册这些方法组。</para>
    /// </summary>
    /// <param name="tools">Tools every session registers. <para>会话注册的工具。</para></param>
    /// <param name="resources">Resources and resource templates every session serves; <see langword="null"/>
    /// or empty registers none. <para>会话服务的资源与资源模板；为 <see langword="null"/> 或空时不注册。</para></param>
    /// <param name="loggerFactory">Optional SDK diagnostics logger factory. <para>可选的 SDK 诊断日志工厂。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> is <see langword="null"/>.
    /// <para><paramref name="tools"/> 为 <see langword="null"/>。</para></exception>
    public McpHttpServerHost(
        IReadOnlyList<McpServerTool> tools,
        IReadOnlyList<McpServerResource>? resources = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
        _resources = resources ?? [];
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger("UotanStudio.Mcp");
    }

    /// <summary>Gets a value indicating whether the port is currently served.
    /// <para>获取当前是否正在监听。</para></summary>
    /// <value><see langword="true"/> between a successful <see cref="Start"/> and the matching <see cref="StopAsync"/> or disposal.
    /// <para>在 <see cref="Start"/> 成功与对应的 <see cref="StopAsync"/> 或释放之间为 <see langword="true"/>。</para></value>
    public bool IsRunning { get; private set; }

    /// <summary>Gets the bound loopback port (resolved value when port 0 was requested).
    /// <para>获取已绑定的回环端口（请求 0 时为系统分配的实际端口）。</para></summary>
    /// <value>The port clients use in <c>http://127.0.0.1:&lt;port&gt;/</c>; <c>0</c> until the listener is bound.
    /// <para>客户端在 <c>http://127.0.0.1:&lt;port&gt;/</c> 中使用的端口；监听器绑定前为 <c>0</c>。</para></value>
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
            var session = new Session(listener, transport, McpServer.Create(transport, McpServerHost.CreateOptions(_tools, _resources), _loggerFactory), cancellation);

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
    /// <returns>A task that completes after the accept and server loops have finished and the server and transport have been disposed; disposal errors are absorbed. A call with no active session completes immediately.
    /// <para>在接收循环与会话循环结束、服务器与传输均已释放之后完成的任务；释放期间的错误会被吸收。没有活动会话时立即完成。</para></returns>
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

    /// <summary>
    /// Stops the listener if one is active and marks the host unusable for further starts.
    /// <para>若监听器仍在运行则停止它，并使宿主不能再被启动。</para>
    /// </summary>
    /// <returns>A task that completes once the session has unwound; repeat calls return immediately without extra work.
    /// <para>会话收敛后完成的任务；重复调用会立即返回且不再执行额外工作。</para></returns>
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