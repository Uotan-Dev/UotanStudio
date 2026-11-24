using System.Diagnostics;

namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// Tunable session parameters. Every value is bounds-checked when the options object is created, so a bad value
/// fails before any device I/O.
/// <para>可调的会话参数。每个取值在创建选项对象时做边界校验，坏取值在任何设备 I/O 之前就失败。</para>
/// </summary>
/// <param name="LoaderBlockSize">Chunk size of FDL1/FDL2 uploads; the vendor tools use 528 bytes.
/// <para>FDL1/FDL2 上传分块；厂商工具使用 528 字节。</para></param>
/// <param name="StorageBlockSize">Chunk size of storage reads and writes; larger values reduce round trips but
/// some FDL2 builds reject them.
/// <para>存储读写分块；更大分块减少往返，但部分 FDL2 会拒绝。</para></param>
/// <param name="CommandTimeoutMs">Timeout of one ordinary BSL command exchange.
/// <para>一次普通 BSL 命令交互的超时。</para></param>
/// <param name="StorageChunkTimeoutMs">Timeout of one storage read or write chunk.
/// <para>一个存储读写分块的超时。</para></param>
/// <param name="EraseTimeoutMs">Timeout of one erase exchange. <para>一次擦除交互的超时。</para></param>
/// <param name="Fdl1StartDelayMs">Wait after the Boot ROM exec before the FDL1 handshake starts.
/// <para>Boot ROM 执行应答后、FDL1 握手开始前的等待。</para></param>
/// <param name="EntryProbeTimeoutMs">Budget of the first CHECK_BAUD probe on an unidentified link.
/// <para>未识别链路上第一次 CHECK_BAUD 探测的预算。</para></param>
/// <param name="CheckBaudRetries">CHECK_BAUD repeats before the FDL handshake gives up.
/// <para>FDL 握手在放弃前重复 CHECK_BAUD 的次数。</para></param>
public sealed record UnisocSessionOptions(
    int LoaderBlockSize = UnisocConstants.DefaultLoaderBlockSize,
    int StorageBlockSize = UnisocConstants.DefaultStorageBlockSize,
    int CommandTimeoutMs = UnisocConstants.DefaultCommandTimeoutMs,
    int StorageChunkTimeoutMs = UnisocConstants.StorageChunkTimeoutMs,
    int EraseTimeoutMs = UnisocConstants.EraseTimeoutMs,
    int Fdl1StartDelayMs = UnisocConstants.Fdl1StartDelayMs,
    int EntryProbeTimeoutMs = UnisocConstants.EntryProbeTimeoutMs,
    int CheckBaudRetries = UnisocConstants.CheckBaudRetries)
{
    /// <summary>Validates every value; called by the sessions before any transport is touched.
    /// <para>校验每个取值；会话在触碰任何传输之前调用它。</para></summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its documented range.
    /// <para>某个取值超出文档范围。</para></exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(LoaderBlockSize, 64);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LoaderBlockSize, 2048);
        ArgumentOutOfRangeException.ThrowIfLessThan(StorageBlockSize, 64);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(StorageBlockSize, UnisocConstants.MaximumStorageBlockSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommandTimeoutMs, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(StorageChunkTimeoutMs, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(EraseTimeoutMs, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(Fdl1StartDelayMs, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Fdl1StartDelayMs, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(EntryProbeTimeoutMs, 50);
        ArgumentOutOfRangeException.ThrowIfLessThan(CheckBaudRetries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CheckBaudRetries, 20);
    }
}

/// <summary>
/// Base BSL session: owns the transport seam, the per-stage checksum and transcode state, the deadline-bounded
/// receive loop and the fault semantics. A faulted session has closed its transport and refuses every further
/// command; the caller reconnects instead of retrying.
/// <para>BSL 会话基类：持有传输接缝、分阶段校验与转义状态、有截止时间的接收循环与失效语义。失效会话已关闭
/// 传输并拒绝一切后续命令；调用方应重新连接而不是重试。</para>
/// </summary>
internal class UnisocSession : IDisposable
{
    private const int ReceiveChunkSize = 4096;

    private readonly IUnisocTransport _transport;
    private readonly UnisocSessionOptions _options;
    private readonly UnisocFrameParser _parser = new();
    private readonly byte[] _receiveBuffer = new byte[ReceiveChunkSize];
    private UnisocChecksumKind _checksumKind = UnisocChecksumKind.Sum;
    private bool _transcode = true;
    private bool _faulted;
    private bool _disposed;

    /// <summary>Creates a session over an already-opened transport.
    /// <para>在已打开的传输之上创建会话。</para></summary>
    /// <param name="transport">The opened transport; the session owns it and closes it on fault or dispose.
    /// <para>已打开的传输；会话持有它并在失效或释放时关闭。</para></param>
    /// <param name="options">Session parameters. <para>会话参数。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is <see langword="null"/>.
    /// <para><paramref name="transport"/> 为 <see langword="null"/>。</para></exception>
    public UnisocSession(IUnisocTransport transport, UnisocSessionOptions options)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options;
        _options.Validate();
    }

    /// <summary>Gets the checksum algorithm of the current stage.
    /// <para>获取当前阶段的校验算法。</para></summary>
    protected UnisocChecksumKind ChecksumKind => _checksumKind;

    /// <summary>Gets or sets whether frames are escape-transcoded; only turning this off after a
    /// <c>DISABLE_TRANSCODE</c> acknowledgement is valid.
    /// <para>获取或设置帧是否做转义变换；只有在 <c>DISABLE_TRANSCODE</c> 得到确认后才允许关闭。</para></summary>
    protected internal bool Transcode => _transcode;

    /// <summary>Gets the validated session options. <para>获取已校验的会话参数。</para></summary>
    protected UnisocSessionOptions Options => _options;

    /// <summary>Gets the transport the session owns, for stage handshakes that need raw byte control.
    /// <para>获取会话持有的传输，供需要原始字节控制的阶段握手使用。</para></summary>
    protected IUnisocTransport Transport => _transport;

    /// <summary>Reports whether the session has faulted or been disposed.
    /// <para>报告会话是否已失效或释放。</para></summary>
    protected bool Unusable => _faulted || _disposed;

    /// <summary>Switches the active checksum algorithm; only the handshake may call this.
    /// <para>切换生效的校验算法；只有握手可以调用。</para></summary>
    protected void SetChecksumKind(UnisocChecksumKind kind) => _checksumKind = kind;

    /// <summary>Turns the escape transcoding off; only a <c>DISABLE_TRANSCODE</c> acknowledgement may call this.
    /// <para>关闭转义变换；只有 <c>DISABLE_TRANSCODE</c> 的确认可以调用。</para></summary>
    protected void SetTranscodeDisabled()
    {
        if (!_transcode)
        {
            throw new InvalidOperationException("The transcode is already disabled.");
        }

        _transcode = false;
    }

    /// <summary>
    /// Sends one command frame and awaits its reply, draining asynchronous log frames on the way. An unexpected
    /// reply, a malformed frame or a timeout faults the session and closes the transport.
    /// <para>发送一条命令帧并等待其应答，途中排空异步日志帧。非预期应答、坏帧或超时都会使会话失效并关闭传输。</para>
    /// </summary>
    /// <param name="command">Command number. <para>命令号。</para></param>
    /// <param name="payload">Payload bytes. <para>负载字节。</para></param>
    /// <param name="timeoutMilliseconds">Exchange timeout. <para>交互超时。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>The reply frame. <para>应答帧。</para></returns>
    /// <exception cref="UnisocProtocolException">The reply was not an <c>ACK</c>, or the link timed out.
    /// <para>应答不是 <c>ACK</c>，或链路超时。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    protected internal async Task<UnisocFrame> SendAndExpectAckAsync(UnisocCommand command, ReadOnlyMemory<byte> payload,
        int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var reply = await ExchangeAsync(command, payload, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        if ((UnisocReply)reply.Type != UnisocReply.Ack)
        {
            Fault();
            throw new UnisocProtocolException(
                $"The device replied 0x{reply.Type:X4} to command 0x{(ushort)command:X4} instead of ACK.");
        }

        return reply;
    }

    /// <summary>
    /// Sends one command frame and collects one reply. <para>发送一条命令帧并收取一条应答。</para>
    /// </summary>
    /// <param name="command">Command number. <para>命令号。</para></param>
    /// <param name="payload">Payload bytes. <para>负载字节。</para></param>
    /// <param name="timeoutMilliseconds">Exchange timeout. <para>交互超时。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>The reply frame. <para>应答帧。</para></returns>
    /// <exception cref="UnisocProtocolException">The link timed out or the session was already unusable.
    /// <para>链路超时，或会话已不可用。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    protected internal async Task<UnisocFrame> ExchangeAsync(UnisocCommand command, ReadOnlyMemory<byte> payload,
        int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (Unusable)
        {
            throw new UnisocProtocolException("The Unisoc session has faulted and must be reopened.");
        }

        var frame = UnisocFrames.EncodeCommand(command, payload.Span, _checksumKind, _transcode);
        try
        {
            await _transport.WriteAsync(frame, cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Fault();
            throw;
        }
        catch
        {
            Fault();
            throw;
        }

        return await ReceiveReplyAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives one reply, draining <c>BSL_REP_LOG</c> frames. <para>接收一条应答，排空 <c>BSL_REP_LOG</c> 帧。</para>
    /// </summary>
    /// <param name="timeoutMilliseconds">Deadline for the whole reply.
    /// <para>整条应答的截止时间。</para></param>
    /// <param name="cancellationToken">Cancels the wait. <para>取消等待。</para></param>
    /// <returns>The reply frame. <para>应答帧。</para></returns>
    /// <exception cref="UnisocProtocolException">The deadline passed, a frame was malformed or the reply was a
    /// terminal error. <para>截止时间已过、帧格式非法，或应答是终结性错误。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    private async Task<UnisocFrame> ReceiveReplyAsync(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + timeoutMilliseconds * Stopwatch.Frequency / 1000;
        while (true)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                Fault();
                throw;
            }

            var remainingMs = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (remainingMs <= 0)
            {
                Fault();
                throw new UnisocProtocolException("The Unisoc link timed out waiting for a reply.");
            }

            int read;
            try
            {
                read = await _transport.ReadAsync(_receiveBuffer.AsMemory(0, ReceiveChunkSize), cancellationToken,
                    remainingMs).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Fault();
                throw;
            }
            catch
            {
                Fault();
                throw;
            }

            if (read <= 0)
            {
                Fault();
                throw new UnisocProtocolException("The Unisoc link timed out waiting for a reply.");
            }

            _parser.Append(_receiveBuffer.AsSpan(0, read));
            while (true)
            {
                UnisocFrame frame;
                UnisocParseStatus status;
                try
                {
                    status = _parser.TryRead(_checksumKind, _transcode, out frame);
                }
                catch (UnisocProtocolException)
                {
                    Fault();
                    throw;
                }

                if (status == UnisocParseStatus.Incomplete)
                {
                    break;
                }

                if (status == UnisocParseStatus.Malformed)
                {
                    Fault();
                    throw new UnisocProtocolException("The Unisoc link delivered a malformed frame.");
                }

                if ((UnisocReply)frame.Type == UnisocReply.Log)
                {
                    continue;
                }

                return frame;
            }
        }
    }

    /// <summary>
    /// Sends raw bytes and collects one frame under the given checksum algorithm; used by the stage handshakes that
    /// must control both sides of an exchange. A frame that fails this algorithm's checksum is reported as
    /// <see langword="null"/> without faulting, so the caller can retry under the other algorithm.
    /// <para>发送原始字节并按给定校验算法收取一帧；供需要同时控制交互两侧的阶段握手使用。未通过该校验算法的帧
    /// 以 <see langword="null"/> 报告而不判失效，调用方可改用另一算法重试。</para>
    /// </summary>
    /// <param name="bytes">Raw wire bytes; an empty array writes nothing.
    /// <para>原始线上字节；空数组不写入。</para></param>
    /// <param name="kind">Checksum algorithm to verify with. <para>用于验证的校验算法。</para></param>
    /// <param name="timeoutMilliseconds">Deadline for the reply. <para>应答截止时间。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>The reply frame, or <see langword="null"/> when the deadline passed or the bytes did not verify
    /// under this algorithm. <para>应答帧；截止时间已过或字节未通过该校验算法时为 <see langword="null"/>。</para></returns>
    protected async Task<UnisocFrame?> ProbeAsync(byte[] bytes, UnisocChecksumKind kind, int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (Unusable)
        {
            throw new UnisocProtocolException("The Unisoc session has faulted and must be reopened.");
        }

        if (bytes.Length > 0)
        {
            var previousKind = _checksumKind;
            _checksumKind = kind;
            try
            {
                await _transport.WriteAsync(bytes, cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Fault();
                throw;
            }
            catch
            {
                Fault();
                throw;
            }
            finally
            {
                _checksumKind = previousKind;
            }
        }

        var (frame, malformed) = await CollectReplyAsync(kind, timeoutMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        if (malformed)
        {
            Fault();
            throw new UnisocProtocolException("The Unisoc link delivered a malformed frame during probing.");
        }

        return frame;
    }

    /// <summary>
    /// Collects one frame from bytes already buffered plus further reads within the deadline, verifying under the
    /// given algorithm. The buffer is preserved across calls, so one exchange can be parsed under both algorithms.
    /// <para>在截止时间内从已缓冲字节与后续读取中收取一帧，按给定算法验证。缓冲跨调用保留，同一次交互可以按两种
    /// 算法各解析一次。</para>
    /// </summary>
    /// <param name="kind">Checksum algorithm to verify with. <para>用于验证的校验算法。</para></param>
    /// <param name="timeoutMilliseconds">Deadline for the reply. <para>应答截止时间。</para></param>
    /// <param name="cancellationToken">Cancels the wait. <para>取消等待。</para></param>
    /// <returns>The frame, or <see langword="null"/> with the flag telling silence (<see langword="false"/>) from
    /// a checksum mismatch (<see langword="true"/>) apart.
    /// <para>帧；为 <see langword="null"/> 时由标志区分静默（<see langword="false"/>）与校验不匹配
    /// （<see langword="true"/>）。</para></returns>
    protected internal async Task<(UnisocFrame? Frame, bool Malformed)> CollectReplyAsync(
        UnisocChecksumKind kind, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (Unusable)
        {
            throw new UnisocProtocolException("The Unisoc session has faulted and must be reopened.");
        }

        var deadline = Stopwatch.GetTimestamp() + timeoutMilliseconds * Stopwatch.Frequency / 1000;
        while (true)
        {
            while (true)
            {
                UnisocFrame frame;
                UnisocParseStatus status;
                try
                {
                    status = _parser.TryRead(kind, _transcode, out frame);
                }
                catch (UnisocProtocolException)
                {
                    Fault();
                    throw;
                }

                if (status == UnisocParseStatus.Incomplete)
                {
                    break;
                }

                if (status == UnisocParseStatus.Malformed)
                {
                    return (null, true);
                }

                if ((UnisocReply)frame.Type == UnisocReply.Log)
                {
                    continue;
                }

                return (frame, false);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                Fault();
                throw;
            }

            var remainingMs = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (remainingMs <= 0)
            {
                return (null, false);
            }

            int read;
            try
            {
                read = await _transport.ReadAsync(_receiveBuffer.AsMemory(0, ReceiveChunkSize), cancellationToken,
                    remainingMs).ConfigureAwait(false);
            }
            catch
            {
                Fault();
                throw;
            }

            if (read <= 0)
            {
                return (null, false);
            }

            try
            {
                _parser.Append(_receiveBuffer.AsSpan(0, read));
            }
            catch (UnisocProtocolException)
            {
                Fault();
                throw;
            }
        }
    }

    /// <summary>Discards bytes buffered by failed probe parses; the handshake calls this after a stage is confirmed.
    /// <para>丢弃失败探测解析缓冲的字节；握手在阶段确认后调用它。</para></summary>
    protected internal void DiscardBuffered() => _parser.Clear();

    /// <summary>Sends a command frame without awaiting any reply, for the CHECK_BAUD probe.
    /// <para>发送命令帧而不等待应答，供 CHECK_BAUD 探测使用。</para></summary>
    /// <param name="command">Command number. <para>命令号。</para></param>
    /// <param name="payload">Payload bytes. <para>负载字节。</para></param>
    /// <param name="timeoutMilliseconds">Write timeout. <para>写入超时。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消写入。</para></param>
    protected async Task SendCommandAsync(UnisocCommand command, ReadOnlyMemory<byte> payload,
        int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (Unusable)
        {
            throw new UnisocProtocolException("The Unisoc session has faulted and must be reopened.");
        }

        var frame = UnisocFrames.EncodeCommand(command, payload.Span, _checksumKind, _transcode);
        await _transport.WriteAsync(frame, cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Faults the session: the transport is disposed and every later command is refused. Dispose failures are
    /// swallowed, the fault itself is the outcome.
    /// <para>使会话失效：释放传输并拒绝后续一切命令。释放失败被吞掉，失效本身就是结果。</para>
    /// </summary>
    protected void Fault()
    {
        if (_faulted)
        {
            return;
        }

        _faulted = true;
        try
        {
            _transport.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>Releases the transport. <para>释放传输。</para></summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _faulted = true;
        _transport.Dispose();
    }
}