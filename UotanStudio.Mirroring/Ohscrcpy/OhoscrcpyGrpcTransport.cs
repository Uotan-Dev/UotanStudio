using System.Net;
using System.Net.Http.Headers;

namespace UotanStudio.Mirroring;

/// <summary>
/// Opens the device gRPC endpoints used by the pushed capture library. Implementations must not expose device
/// sockets to the mirroring layer; the HTTP/2 connection runs over a loopback port that Comm already forwarded.
/// <para>打开推送采集库使用的设备 gRPC 端点。实现不得向投屏层暴露设备套接字；HTTP/2 连接只经过 Comm 已转发的
/// 回环端口。</para>
/// </summary>
internal interface IOhoscrcpyVideoTransport : IDisposable
{
    /// <summary>Starts the server-streaming video call and returns the response body.
    /// <para>启动服务端流式视频调用并返回响应正文。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the HTTP/2 request. <para>取消 HTTP/2 请求。</para></param>
    /// <param name="budgetMs">Optional readiness budget for the first call; zero uses the transport default.
    /// <para>首次调用的可选就绪预算；为零时使用传输层默认值。</para></param>
    /// <returns>The gRPC response body carrying length-prefixed messages.
    /// <para>承载带长度前缀消息的 gRPC 响应正文。</para></returns>
    Stream OpenVideoStream(CancellationToken cancellationToken, int budgetMs = 0);

    /// <summary>Sends the unary key-frame request. <para>发送一元关键帧请求。</para></summary>
    /// <param name="cancellationToken">Cancels the HTTP/2 request. <para>取消 HTTP/2 请求。</para></param>
    /// <exception cref="IOException">The device rejected the request. <para>设备拒绝了该请求。</para></exception>
    void RequestKeyFrame(CancellationToken cancellationToken);
}

/// <summary>
/// Standard gRPC-over-HTTP/2 client for the device extension, using a prior-knowledge cleartext HTTP/2
/// connection. <para>面向设备扩展的标准 gRPC-over-HTTP/2 客户端，使用明文 HTTP/2 的先验知识连接。</para>
/// </summary>
/// <remarks>
/// <para>
/// The contract is small enough that a full gRPC stack would add reflection and trimming surface for two
/// unary calls and one server stream; this class frames the empty request and reads the response messages
/// directly. It never sends or accepts compressed messages and bounds every payload.
/// </para>
/// <para>
/// 契约足够小，引入完整 gRPC 栈只会为两个一元调用和一个服务端流增加反射与裁剪面；本类直接为空的请求分帧并读取
/// 响应消息。它不发送也不接受压缩消息，并对每个负载设界。
/// </para>
/// </remarks>
internal sealed class OhoscrcpyGrpcTransport : IOhoscrcpyVideoTransport
{
    private const int DefaultVideoReadyBudgetMs = 15_000;
    private const int FirstRetryDelayMs = 250;
    private const int MaximumRetryDelayMs = 1_000;

    private readonly HttpClient _client;
    private readonly IDisposable? _forward;
    private HttpResponseMessage? _videoResponse;
    private int _disposed;

    internal OhoscrcpyGrpcTransport(int loopbackPort, IDisposable? forward = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(loopbackPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(loopbackPort, 65535);
        _forward = forward;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{loopbackPort}/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    /// <summary>Starts the server-streaming <c>onStart</c> call.
    /// <para>启动服务端流式 <c>onStart</c> 调用。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the request. <para>取消请求。</para></param>
    /// <param name="budgetMs">Readiness budget for the first call, in milliseconds; zero uses
    /// <see cref="DefaultVideoReadyBudgetMs"/>. <para>首次调用的就绪预算（毫秒）；为零时使用
    /// <see cref="DefaultVideoReadyBudgetMs"/>。</para></param>
    /// <returns>The gRPC response body. <para>gRPC 响应正文。</para></returns>
    /// <exception cref="IOException">The device never accepted the call: the capture extension was not serving its
    /// gRPC socket yet, or the forwarded connection was closed. The caller may retry mirroring.
    /// <para>设备始终没有接受该调用：采集扩展尚未开始服务其 gRPC socket，或转发连接被关闭。调用方可以重新开始投屏。</para></exception>
    /// <exception cref="NotSupportedException">The device answered with an unimplemented call, which means the running
    /// capture library does not expose this gRPC contract.
    /// <para>设备以"调用未实现"应答，说明正在运行的采集库没有提供该 gRPC 契约。</para></exception>
    public Stream OpenVideoStream(CancellationToken cancellationToken, int budgetMs = 0)
    {
        var budget = budgetMs > 0 ? budgetMs : DefaultVideoReadyBudgetMs;
        var deadline = Environment.TickCount64 + budget;
        var delay = FirstRetryDelayMs;
        Exception? last = null;
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = SendAsync(OhscrcpyConstants.VideoCallPath, cancellationToken)
                    .GetAwaiter().GetResult();
                try
                {
                    var stream = response.Content.ReadAsStream(cancellationToken);
                    Interlocked.Exchange(ref _videoResponse, response)?.Dispose();
                    return stream;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException
                && error is not ObjectDisposedException
                && !OhscrcpyFailure.IsCapabilityFailure(error))
            {
                last = error;
                if (Environment.TickCount64 >= deadline)
                {
                    break;
                }

                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, MaximumRetryDelayMs);
            }
        }

        throw new IOException(
            OhscrcpyFailure.VideoPrefix
            + $"the video gRPC endpoint did not answer within {budget} ms: {last?.Message}. Start mirroring again; "
            + "if it keeps failing, check the capture-extension log on the device. "
            + OhscrcpyFailure.VideoPrefix
            + $"视频 gRPC 端点未在 {budget} ms 内响应：{last?.Message}。请重新开始投屏；若持续失败，请检查设备上的采集扩展日志。",
            last);
    }

    /// <summary>Sends the unary <c>onRequestIDRFrame</c> call and waits for its reply.
    /// <para>发送一元 <c>onRequestIDRFrame</c> 调用并等待回复。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the request. <para>取消请求。</para></param>
    /// <exception cref="IOException">The device refused the call. <para>设备拒绝了该调用。</para></exception>
    public void RequestKeyFrame(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var response = SendAsync(OhscrcpyConstants.KeyFrameCallPath, cancellationToken)
            .GetAwaiter().GetResult();
        _ = response.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(OhscrcpyGrpcProtocol.BuildEmptyRequest()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("te", "trailers");
        var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            if (OhscrcpyFailure.IsUnimplementedStatus(status))
            {
                throw new NotSupportedException(
                    OhscrcpyFailure.CapabilityPrefix
                    + $"the running capture library does not implement {path} (HTTP {status}); it does not match this "
                    + "UI Test version. Replace the ohos/device capture library or use a supported device. "
                    + OhscrcpyFailure.CapabilityPrefix
                    + $"正在运行的采集库没有实现 {path}（HTTP {status}），与当前 UI Test 版本不匹配。请替换 ohos/device "
                    + "下的采集库，或改用受支持的设备。");
            }

            throw new IOException(
                OhscrcpyFailure.VideoPrefix
                + $"the device gRPC call {path} failed with HTTP {status}. "
                + OhscrcpyFailure.VideoPrefix
                + $"设备 gRPC 调用 {path} 以 HTTP {status} 失败。");
        }

        return response;
    }

    /// <summary>Releases the HTTP/2 client and any held video response.
    /// <para>释放 HTTP/2 客户端与持有的视频响应。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _videoResponse?.Dispose();
        _client.Dispose();
        _forward?.Dispose();
    }
}