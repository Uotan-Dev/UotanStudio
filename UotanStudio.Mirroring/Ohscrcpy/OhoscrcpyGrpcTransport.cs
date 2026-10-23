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
    /// <returns>The gRPC response body carrying length-prefixed messages.
    /// <para>承载带长度前缀消息的 gRPC 响应正文。</para></returns>
    Stream OpenVideoStream(CancellationToken cancellationToken);

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
    /// <returns>The gRPC response body. <para>gRPC 响应正文。</para></returns>
    /// <exception cref="IOException">The device refused the call or returned a non-success status.
    /// <para>设备拒绝调用或返回非成功状态。</para></exception>
    public Stream OpenVideoStream(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
            var status = response.StatusCode;
            response.Dispose();
            throw new IOException($"The device gRPC call {path} failed with HTTP {(int)status}.");
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