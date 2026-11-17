using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Speaks the NVIDIA Tegra recovery mode (RCM, USB APX) protocol over one bulk transport: it queries the boot
/// ROM version words, synchronises the link and downloads the miniloader that turns the boot ROM into the nv3p
/// endpoint.
/// <para>在一条批量传输之上讲 NVIDIA Tegra 恢复模式（RCM，USB APX）协议：查询 boot ROM 的版本字、同步链路，
/// 并下载把 boot ROM 变成 nv3p 端点的 miniloader。</para>
/// </summary>
/// <remarks>
/// The session never signs a message on its own. A device in a secure operating mode rejects unsigned downloads,
/// and signing needs material the host must supply, so the download methods throw until a key is configured rather
/// than sending a frame the device will refuse.
/// <para>本会话绝不自行对消息签名。运行在安全模式下的设备会拒绝未签名下载，而签名需要主机提供密钥材料，因此下载方法
/// 在配置密钥之前直接抛出，而不是发出设备必然拒绝的帧。</para>
/// </remarks>
public sealed class TegraRcmSession : IDisposable
{
    /// <summary>Default per-read timeout in milliseconds.
    /// <para>默认的每次读取超时时间（毫秒）。</para></summary>
    public const int DefaultTimeoutMs = 10_000;

    /// <summary>Largest payload one RCM download message carries.
    /// <para>单条 RCM 下载消息携带的最大负载。</para></summary>
    public const int MaximumDownloadChunkLength = 0x4000;

    private readonly int _timeoutMs;
    private bool _disposed;

    /// <summary>
    /// Initializes a session over an already-opened transport.
    /// <para>在已打开的传输之上初始化会话。</para>
    /// </summary>
    /// <param name="transport">The transport the session runs on; the session never disposes it.
    /// <para>会话所使用的传输；会话绝不释放它。</para></param>
    /// <param name="timeoutMs">Optional per-read timeout; defaults to <see cref="DefaultTimeoutMs"/>.
    /// <para>可选的每次读取超时；默认取 <see cref="DefaultTimeoutMs"/>。</para></param>
    public TegraRcmSession(ICommTransport transport, int? timeoutMs = null)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeoutMs = timeoutMs ?? DefaultTimeoutMs;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_timeoutMs);
    }

    /// <summary>The protocol name this session speaks. <para>本会话所讲的协议名。</para></summary>
    /// <value>The constant <c>tegra</c>. <para>常量 <c>tegra</c>。</para></value>
    public string ProtocolName => "tegra";

    /// <summary>The transport the session runs on. <para>会话所使用的传输。</para></summary>
    /// <value>The transport supplied at construction. <para>构造时传入的传输。</para></value>
    public ICommTransport Transport { get; }

    /// <summary>Whether the session is usable. <para>会话是否可用。</para></summary>
    /// <value><see langword="true"/> while undisposed and the transport reports a connection.
    /// <para>会话未释放且传输报告已连接时为 <see langword="true"/>。</para></value>
    public bool IsConnected => !_disposed && Transport.IsConnected;

    /// <summary>
    /// Reads the RCM protocol version the boot ROM speaks (<c>RCM_CMD_QUERY_RCM_VERSION</c>).
    /// <para>读取 boot ROM 所讲的 RCM 协议版本（<c>RCM_CMD_QUERY_RCM_VERSION</c>）。</para>
    /// </summary>
    /// <param name="chip">The target generation, which selects the message layout.
    /// <para>目标代际，用于选择消息布局。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>The packed version word. <para>打包后的版本字。</para></returns>
    /// <exception cref="TegraProtocolException">The device reported a status instead of a version.
    /// <para>设备上报了状态而不是版本。</para></exception>
    public Task<uint> QueryRcmVersionAsync(TegraChip chip, CancellationToken cancellationToken = default)
        => SendQueryAsync(chip, TegraRcmOpcode.QueryRcmVersion, cancellationToken);

    /// <summary>
    /// Reads the boot ROM version (<c>RCM_CMD_QUERY_BR_VERSION</c>).
    /// <para>读取 boot ROM 版本（<c>RCM_CMD_QUERY_BR_VERSION</c>）。</para>
    /// </summary>
    /// <param name="chip">The target generation. <para>目标代际。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>The packed boot ROM version word. <para>打包后的 boot ROM 版本字。</para></returns>
    public Task<uint> QueryBrVersionAsync(TegraChip chip, CancellationToken cancellationToken = default)
        => SendQueryAsync(chip, TegraRcmOpcode.QueryBootRomVersion, cancellationToken);

    /// <summary>
    /// Reads the boot device version (<c>RCM_CMD_QUERY_BD_VERSION</c>).
    /// <para>读取引导设备版本（<c>RCM_CMD_QUERY_BD_VERSION</c>）。</para>
    /// </summary>
    /// <param name="chip">The target generation. <para>目标代际。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>The packed boot device version word. <para>打包后的引导设备版本字。</para></returns>
    public Task<uint> QueryBdVersionAsync(TegraChip chip, CancellationToken cancellationToken = default)
        => SendQueryAsync(chip, TegraRcmOpcode.QueryBootDeviceVersion, cancellationToken);

    /// <summary>
    /// Sends the synchronisation command (<c>RCM_CMD_SYNC</c>).
    /// <para>发送同步命令（<c>RCM_CMD_SYNC</c>）。</para>
    /// </summary>
    /// <param name="chip">The target generation. <para>目标代际。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>The status word the device answered. <para>设备应答的状态字。</para></returns>
    public async Task<uint> SyncAsync(TegraChip chip, CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync(chip, TegraRcmOpcode.Sync, ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return reply.Value;
    }

    /// <summary>
    /// Downloads the miniloader (<c>RCM_CMD_DL_MINILOADER</c>), which turns the boot ROM into the nv3p endpoint.
    /// <para>下载 miniloader（<c>RCM_CMD_DL_MINILOADER</c>），把 boot ROM 变成 nv3p 端点。</para>
    /// </summary>
    /// <param name="miniloader">The miniloader image. <para>miniloader 镜像。</para></param>
    /// <param name="chip">The target generation. <para>目标代际。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels the download. <para>取消本次下载。</para></param>
    /// <returns>A task that completes when the whole image was written and acknowledged.
    /// <para>整个镜像写出并得到应答时结束的任务。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="miniloader"/> is empty.
    /// <para><paramref name="miniloader"/> 为空。</para></exception>
    /// <exception cref="NotSupportedException">The run needs a signing key and none was configured.
    /// <para>本次运行需要签名密钥但未配置。</para></exception>
    /// <exception cref="TegraProtocolException">The device refused the download.
    /// <para>设备拒绝本次下载。</para></exception>
    public async Task DownloadMiniloaderAsync(
        ReadOnlyMemory<byte> miniloader,
        TegraChip chip,
        IProgress<TegraRcmProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (miniloader.IsEmpty)
        {
            throw new ArgumentException("The miniloader image is empty.", nameof(miniloader));
        }

        var total = miniloader.Length;
        var sent = 0;
        progress?.Report(new TegraRcmProgress("download", 0, total,
            $"Downloading {total} bytes of miniloader."));

        while (sent < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(MaximumDownloadChunkLength, total - sent);
            var chunk = miniloader.Slice(sent, chunkLength);
            var reply = await SendAsync(chip, TegraRcmOpcode.DownloadMiniloader, chunk,
                ReadOnlyMemory<byte>.Empty, cancellationToken, messageLengthOverride: total)
                .ConfigureAwait(false);
            ThrowIfStatusError(reply, "The device refused the miniloader download");
            sent += chunkLength;
            progress?.Report(new TegraRcmProgress("download", sent, total,
                $"Downloaded {sent} of {total} miniloader bytes."));
        }

        progress?.Report(new TegraRcmProgress("done", total, total, "Miniloader download complete."));
    }

    /// <summary>Releases the session without disposing the transport it was given.
    /// <para>释放会话但不释放在构造时传入的传输。</para></summary>
    public void Dispose() => _disposed = true;

    private async Task<uint> SendQueryAsync(TegraChip chip, TegraRcmOpcode opcode,
        CancellationToken cancellationToken)
    {
        var reply = await SendAsync(chip, opcode, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);
        ThrowIfStatusError(reply, $"The device refused the {opcode} query");
        return reply.Value;
    }

    private async Task<TegraRcmReply> SendAsync(
        TegraChip chip,
        TegraRcmOpcode opcode,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> args,
        CancellationToken cancellationToken,
        int? messageLengthOverride = null)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var frame = await TegraRcmProtocol.CreateAsync(chip, opcode, args, payload, cancellationToken)
            .ConfigureAwait(false);
        if (frame.Length > TegraRcmProtocol.MaximumFrameLength)
        {
            throw new InvalidOperationException(
                $"The RCM frame is {frame.Length} bytes, above the {TegraRcmProtocol.MaximumFrameLength}-byte limit.");
        }

        var written = Transport.Write(frame, 0, frame.Length, _timeoutMs);
        if (written != frame.Length)
        {
            throw new IOException($"The transport accepted {written} of {frame.Length} RCM frame bytes.");
        }

        return await TegraRcmProtocol.ReadResponseAsync(ReadAsync, _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken)
        => new(Transport.ReadAsync(buffer, offset, length, timeoutMs, cancellationToken));

    private static void ThrowIfStatusError(TegraRcmReply reply, string context)
    {
        // A device answers a successful command with a zero status word; anything else is the device naming the
        // condition it refused on, so the raw value is preserved instead of folded into a known code.
        // 设备以零状态字应答成功的命令；其它取值是设备在说明拒绝原因，因此原样保留原始值而不是并入已知码。
        if (reply.Value == 0)
        {
            return;
        }

        throw new TegraProtocolException($"{context}: the device answered status 0x{reply.Value:X8}.",
            reply.Value, null);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}