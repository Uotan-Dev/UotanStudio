using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Protocols.Fastboot;

public sealed partial class FastbootSession
{
    /// <summary>Upper bound on accumulated INFO lines; a flood beyond this fails the command.</summary>
    private const int MaxInfoLines = 10000;

    /// <summary>
    /// Wall-clock budget for checking whether the device already answered a command frame whose bulk
    /// write reported that no byte was accepted, before that same frame is written again. A bulk write
    /// timeout does not prove the device never saw the frame: the URB may have reached the endpoint and
    /// only its completion been lost, in which case the device is already replying and re-issuing the
    /// command would execute it twice. The probe is bounded so a device that answers nothing still
    /// leaves room for one re-issue inside the same action timeout.
    /// <para>检查设备是否已经应答“零字节被接受”命令帧的墙上时间预算，该检查在重写同一帧之前进行。
    /// 批量写入超时并不能证明设备没收到该帧：URB 可能已抵达端点而仅丢失了完成信号，此时设备正在应答，
    /// 重发该命令会导致它被执行两次。探测是有界的，因此完全沉默的设备仍能在同一动作超时内留出一次重发的时间。</para>
    /// </summary>
    private const int CommandWriteProbeTimeoutMs = 1000;

    /// <summary>
    /// Total number of times one command frame may be written. A zero-byte write is recovered once
    /// (reset then rewrite), so a device that stays silent still fails within two write timeouts
    /// instead of blocking the caller for an unbounded number of attempts.
    /// <para>单个命令帧允许写入的总次数。零字节写入只恢复一次（重置后重写），因此始终沉默的设备仍会在两个
    /// 写入超时内失败，而不是阻塞调用方于无界次数的尝试。</para>
    /// </summary>
    private const int MaxCommandWriteAttempts = 2;

    /// <summary>
    /// Upper bound, in milliseconds, on the pause inserted between two consecutive zero-length
    /// transport reads. Transports are allowed to report "no data yet" as a bare 0 (a USB read that
    /// timed out, a TCP frame whose payload has not arrived, a UDP datagram that carried no bytes),
    /// and a loop that retries such a read without pausing burns a core for the whole response
    /// deadline. The pause grows linearly with the consecutive zero-read count up to this cap, so the
    /// deadline still bounds wall time while CPU stays bounded.
    /// <para>两次连续零长度传输读取之间插入的暂停上限（毫秒）。传输允许把“暂无数据”报告为裸 0
    /// （超时的 USB 读取、负载尚未到达的 TCP 帧、未携带字节的 UDP 数据报），而不加暂停就重试这种读取的
    /// 循环会在整个响应截止时间内烧满一个核心。暂停随连续零读次数线性增长至该上限，因此截止时间仍然约束
    /// 墙上时间，同时 CPU 保持有界。</para>
    /// </summary>
    private const int MaxZeroReadBackoffMs = 5;

    /// <summary>
    /// Sends a raw command string to the device (UTF-8 encoded, max 4096 bytes).
    /// <para>向设备发送原始命令字符串（UTF-8 编码，最大 4096 字节）。</para>
    /// </summary>
    /// <remarks>
    /// A command frame the transport accepted no byte of is not treated as a permanent failure: a bulk
    /// write timeout does not prove the frame was never delivered, so the device is first probed for an
    /// already-delivered reply, and only a silent device has its transport reset and the frame written a
    /// second time. A frame partly accepted is never re-issued, because the device already holds a
    /// truncated command and appending a second copy would corrupt the stream.
    /// <para>传输未接受任何字节的命令帧不会被当作永久失败：批量写入超时并不能证明该帧未被送达，因此先探测
    /// 设备是否已经应答，只有完全沉默的设备才会重置传输并重写该帧一次。已部分接受的帧绝不重发，因为设备
    /// 已持有被截断的命令，再追加一份副本会破坏数据流。</para>
    /// </remarks>
    /// <param name="command">Command text to encode and write as a single frame. <para>要编码并按单帧写入的命令文本。</para></param>
    /// <exception cref="ArgumentException">The UTF-8 encoding exceeds <see cref="FastbootConstants.MaxCommandLength"/> bytes.
    /// <para>UTF-8 编码超过 <see cref="FastbootConstants.MaxCommandLength"/> 字节。</para></exception>
    /// <exception cref="FastbootException">The transport accepted only part of the frame, or accepted none of it on
    /// every attempt while the device stayed silent. <para>传输只接受了帧的一部分，或每次尝试都未接受任何字节且设备始终沉默。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public void SendCommand(string command) => SendCommand(command, timeoutMs: null);

    /// <summary>
    /// Writes one command frame with an explicit transfer timeout, recovering from a zero-byte write.
    /// <para>以显式传输超时写入一个命令帧，并从零字节写入中恢复。</para>
    /// </summary>
    /// <param name="command">Command text to encode and write as a single frame. <para>要编码并按单帧写入的命令文本。</para></param>
    /// <param name="timeoutMs">Transfer timeout in milliseconds; <see langword="null"/> keeps the session timeout.
    /// <para>传输超时毫秒数；为 <see langword="null"/> 时沿用会话超时。</para></param>
    internal void SendCommand(string command, int? timeoutMs)
    {
        ThrowIfDisposed();
        var cmdBytes = Encoding.UTF8.GetBytes(command);
        if (cmdBytes.Length > FastbootConstants.MaxCommandLength)
        {
            throw new ArgumentException($"Command too long ({cmdBytes.Length} > {FastbootConstants.MaxCommandLength})", nameof(command));
        }

        var effectiveTimeoutMs = timeoutMs ?? _timeoutMs;
        for (var attempt = 1; ; attempt++)
        {
            var written = Transport.Write(cmdBytes, 0, cmdBytes.Length, effectiveTimeoutMs);
            if (written == cmdBytes.Length)
            {
                return;
            }

            if (written > 0)
            {
                throw new FastbootException(FastbootResponseState.Fail, $"Short write: {written}/{cmdBytes.Length}");
            }

            var delivered = ProbeDeliveredCommandResponse();
            if (delivered is not null)
            {
                _preReadResponse = delivered;
                return;
            }

            if (attempt >= MaxCommandWriteAttempts)
            {
                throw new FastbootException(FastbootResponseState.Fail,
                    $"Short write: 0/{cmdBytes.Length}; the transport accepted no byte of the '{command}' command frame " +
                    $"on {MaxCommandWriteAttempts} attempt(s) and the device answered no probe either. The device stopped " +
                    "servicing its bulk OUT endpoint: check the cable and port, close any other tool holding the device, " +
                    "and reconnect it.");
            }

            CommTrace.Debug("fastboot",
                $"command frame '{command}' was accepted for 0 of {cmdBytes.Length} bytes; resetting the transport and rewriting it once.");
            ResetTransportAfterCommandWriteFailure();
        }
    }

    /// <summary>
    /// Reports the device reply that proves a command frame reported as unwritten was delivered after all,
    /// or <see langword="null"/> when the device stayed silent for the probe window.
    /// <para>报告证明“未被写入”的命令帧实际已送达的设备应答；设备在探测窗口内保持沉默时返回
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <remarks>
    /// Only a terminal frame the device actually sent counts: a device that received the command answers
    /// OKAY/FAIL/DATA. A probe timeout — or a Fail the parser synthesized because the transport reported itself
    /// disconnected or flooded — proves nothing about delivery, so those return <see langword="null"/> and the
    /// caller may safely reset the pipe and write the frame again. A session disposed underneath the probe (a
    /// concurrent lease release) also answers <see langword="null"/>: the frame then has no device to reach,
    /// and the caller's next attempt reports the session state instead of a probe fault.
    /// <para>只有设备实际发送的终止帧才算数：收到命令的设备会应答 OKAY/FAIL/DATA。探测超时，或解析器因传输
    /// 自称断开/泛洪而合成的 Fail，都不能证明命令已送达，因此返回 <see langword="null"/>，调用方可以安全地
    /// 重置管道并重写该帧。探测期间被并发释放的会话同样返回 <see langword="null"/>：此时该帧已没有可达设备，
    /// 调用方的下一次尝试会报告会话状态而不是探测故障。</para>
    /// </remarks>
    /// <returns>The terminal reply, or <see langword="null"/> on a silent timeout.
    /// <para>终止应答；静默超时时为 <see langword="null"/>。</para></returns>
    private FastbootResponse? ProbeDeliveredCommandResponse()
    {
        FastbootResponse probe;
        try
        {
            probe = ReceiveResponse(CommandWriteProbeTimeoutMs);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        return probe.ReceivedTerminalFrame ? probe : null;
    }

    /// <summary>
    /// Clears the state a reset invalidates after a command frame was written without the device taking it.
    /// <para>在命令帧未被设备接收后清除重置会使其失效的状态。</para>
    /// </summary>
    /// <remarks>
    /// A reset drop whatever the pipe still held, so retained response bytes are discarded and the negotiated
    /// max-download-size is dropped with the re-enumerated device's possible new limit. A reset that itself
    /// fails is swallowed: this runs on the recovery path of an already-failing write, and its own error must
    /// not mask the error being reported to the caller.
    /// <para>重置会丢弃管道中仍持有的内容，因此保留的应答字节被丢弃，已协商的 max-download-size 也随重新枚举
    /// 设备可能的新上限一并失效。重置自身失败会被吞掉：本方法运行在一次已失败写入的恢复路径上，其自身的错误
    /// 不得掩盖正要上报给调用方的错误。</para>
    /// </remarks>
    private void ResetTransportAfterCommandWriteFailure()
    {
        _pendingBytes.Clear();
        InvalidateMaxDownloadSize();
        try
        {
            Transport.Reset();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Receives and parses a fastboot protocol response.
    /// <para>接收并解析 fastboot 协议响应。</para></summary>
    /// <remarks>
    /// Incoming bytes are accumulated at byte level (never re-decoded per packet, so a multi-byte UTF-8 sequence split across packets survives) into a session-held buffer. Frames are consumed from that buffer one at a time, and whatever the transport delivered past a terminal frame is retained for the next call instead of being silently dropped, so a trailing INFO after OKAY desynchronizes neither the stream nor the next command's reply. INFO lines and TEXT content are accumulated onto the returned response; the call returns only at a terminal OKAY/FAIL/DATA frame (or an unrecoverable unknown prefix), with the timeout deadline extended by every consumed INFO/TEXT frame (the fastboot keep-alive) and otherwise fixed from the command start.
    /// <para>入站字节按字节级累积到会话持有的缓冲区（绝不按包重复解码，跨包拆分的 UTF-8 多字节序列得以 保留），帧从该缓冲区逐个消费；传输在终止帧之后送达的字节会保留给下一次调用而非被静默丢弃，因此 OKAY 之后的尾随 INFO 既不会破坏流，也不会错误归因到下一条命令的应答。INFO 行与 TEXT 内容累积到 返回的响应对象上；仅在到达终止 OKAY/FAIL/DATA 帧（或出现不可恢复的未知前缀）时返回。超时截止 时间会因每消费到一帧 INFO/TEXT 而顺延（fastboot 的保活语义），其余情况自命令开始固定不变。</para>
    /// </remarks>
    /// <param name="timeoutMs">Overall receive deadline in milliseconds; when <see langword="null"/> the session timeout is used. Every consumed INFO/TEXT frame restarts the deadline.
    /// <para>整体接收截止时间（毫秒）；为 <see langword="null"/> 时使用会话超时。每消费到一帧 INFO/TEXT 都会重新开始计时。</para></param>
    /// <param name="cancellationToken">Cancels the wait for a terminal frame; the token bounds the total duration even when INFO/TEXT frames keep extending the deadline.
    /// <para>取消对终止帧的等待；即使 INFO/TEXT 帧不断顺延截止时间，令牌仍约束总时长。</para></param>
    /// <returns>The parsed response; <see cref="FastbootResponse.State"/> reports Success/Fail/Data/Timeout/Unknown.
    /// <para>解析后的响应；<see cref="FastbootResponse.State"/> 报告 Success/Fail/Data/Timeout/Unknown。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public FastbootResponse ReceiveResponse(int? timeoutMs = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_preReadResponse is { } preRead)
        {
            _preReadResponse = null;
            return preRead;
        }

        var response = new FastbootResponse();
        var timeout = timeoutMs ?? _timeoutMs;
        var startTimestamp = Stopwatch.GetTimestamp();
        var timeoutTicks = (long)(Stopwatch.Frequency * (timeout / 1000.0));
        var textBytes = 0;
        StringBuilder? textBuffer = null;

        var buffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.ResponseBufferSize);
        try
        {
            var zeroReads = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = 0;
                while (true)
                {
                    var consumed = ConsumeFrame(_pendingBytes.Span, offset, response, ref textBuffer, ref textBytes);
                    if (consumed == 0)
                    {
                        break;
                    }

                    offset += consumed;
                    zeroReads = 0;
                    if (response.State is FastbootResponseState.Info or FastbootResponseState.Text)
                    {
                        startTimestamp = Stopwatch.GetTimestamp();
                    }

                    if (response.State is FastbootResponseState.Success
                        or FastbootResponseState.Fail
                        or FastbootResponseState.Data
                        or FastbootResponseState.Unknown)
                    {
                        _pendingBytes.RemoveFromStart(offset);
                        return response;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (offset > 0)
                {
                    _pendingBytes.RemoveFromStart(offset);
                }

                var remainingTicks = timeoutTicks - (Stopwatch.GetTimestamp() - startTimestamp);
                if (remainingTicks <= 0)
                {
                    break;
                }

                if (!Transport.IsConnected)
                {
                    response.State = FastbootResponseState.Fail;
                    response.Text = "transport disconnected while waiting for a response";
                    return response;
                }

                var readTimeoutMs = Math.Max(1, (int)Math.Min(int.MaxValue,
                    ((remainingTicks * 1000) + Stopwatch.Frequency - 1) / Stopwatch.Frequency));
                var readLen = Transport.Read(buffer, 0, FastbootConstants.ResponseBufferSize, readTimeoutMs);
                if (readLen <= 0)
                {
                    zeroReads++;
                    var backoffMs = Math.Min(MaxZeroReadBackoffMs, zeroReads);
                    Thread.Sleep(backoffMs);
                    continue;
                }

                zeroReads = 0;
                if (readLen > FastbootConstants.MaxPendingResponseBytes - _pendingBytes.Count)
                {
                    ResetTransportAfterAbandon();
                    response.State = FastbootResponseState.Fail;
                    response.Info = $"device response exceeded the {FastbootConstants.MaxPendingResponseBytes}-byte pending buffer limit";
                    response.Text = response.Info;
                    return response;
                }

                _pendingBytes.Append(buffer.AsSpan(0, readLen));
            }

            response.State = FastbootResponseState.Timeout;
            response.Text = "response timeout";
            return response;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Consumes one wire frame from the pending byte buffer starting at <paramref name="offset"/>.
    /// Returns 0 when more bytes are needed. Terminal frames (OKAY/FAIL/DATA) and an unknown prefix
    /// complete the response and consume everything buffered behind them, because one fastboot exchange is
    /// strictly request/response: a bootloader that answers with its whole status buffer leaves stale bytes of
    /// earlier replies behind the terminating NUL, and carrying those forward would shift every later reply by
    /// one command. INFO/TEXT frames are accumulated, recorded on <see cref="FastbootResponse.State"/> as the
    /// non-terminal Info/Text marker and parsing continues. Recording that marker is what keeps the caller from
    /// mistaking an accumulated INFO frame for a completed response: <see cref="FastbootResponseState.Success"/>
    /// is the enum's default member, so a frame that leaves the state untouched reads as a terminal OKAY and
    /// truncates every reply that carries more than one INFO line (getvar:all, snapshot-update hints).
    /// <para>从待解析字节缓冲区的 <paramref name="offset"/> 处消费一帧。字节不足时返回 0。终止帧
    /// （OKAY/FAIL/DATA）与未知前缀会完成响应，并连同其后的全部已缓冲字节一起消费，因为一次 fastboot 交换严格
    /// 是请求/应答：把整个状态缓冲区发出来的 bootloader 会在终止 NUL 之后留下更早应答的陈旧字节，把它们带到后续
    /// 命令会让每个应答错位一条命令。INFO/TEXT 帧被累积，并以非终止的 Info/Text 标记记录到
    /// <see cref="FastbootResponse.State"/> 后继续解析。记录该标记正是阻止调用方把已累积的 INFO 帧误认为
    /// 已完成响应的关键：<see cref="FastbootResponseState.Success"/> 是枚举的默认成员，因此不触碰状态的帧
    /// 会被读成终止的 OKAY，从而截断所有携带多于一行 INFO 的应答（getvar:all、snapshot-update 提示）。</para>
    /// </summary>
    /// <param name="pending">Session-held pending byte buffer. 会话持有的待解析字节缓冲区。</param>
    /// <param name="offset">Parse position inside <paramref name="pending"/>. <paramref name="pending"/> 内的解析位置。</param>
    /// <param name="response">Response being assembled. 正在组装的响应。</param>
    /// <param name="textBuffer">Accumulated TEXT content. 累积的 TEXT 内容。</param>
    /// <param name="textBytes">Total TEXT bytes accumulated so far. 目前累积的 TEXT 字节总数。</param>
    /// <returns>The number of bytes consumed by the frame, or 0 when more input is required.
    /// 该帧消费的字节数；需要更多输入时为 0。</returns>
    private static int ConsumeFrame(ReadOnlySpan<byte> pending, int offset, FastbootResponse response,
        ref StringBuilder? textBuffer, ref int textBytes)
    {
        var available = pending.Length - offset;
        if (available < 4)
        {
            return 0;
        }

        if (!IsKnownPrefixAt(pending, offset))
        {
            var junk = DecodeUtf8(pending, offset, pending.Length);
            response.State = FastbootResponseState.Unknown;
            response.Info = "device sent unknown status code: " + junk;
            response.Text = junk;
            return available;
        }

        if (IsPrefixAt(pending, offset, 'O', 'K', 'A', 'Y') || IsPrefixAt(pending, offset, 'F', 'A', 'I', 'L'))
        {
            var isOkay = pending[offset] == 'O';
            response.State = isOkay ? FastbootResponseState.Success : FastbootResponseState.Fail;
            response.ReceivedTerminalFrame = true;
            var content = ExtractFrameContent(pending, offset + 4);
            response.Info = content;
            response.Text = textBuffer?.ToString() ?? content;
            return available;
        }

        if (IsPrefixAt(pending, offset, 'I', 'N', 'F', 'O') || IsPrefixAt(pending, offset, 'T', 'E', 'X', 'T'))
        {
            var isInfo = pending[offset] == 'I';
            if (isInfo && response.InfoLines.Count >= MaxInfoLines)
            {
                response.State = FastbootResponseState.Fail;
                response.Info = "device sent too many INFO frames";
                response.Text = textBuffer?.ToString() ?? string.Empty;
                return available;
            }

            var contentStart = offset + 4;
            var endIdx = FindInfoTextEnd(pending, contentStart);
            if (endIdx < 0)
            {
                return 0;
            }

            var content = DecodeUtf8(pending, contentStart, endIdx);
            if (isInfo)
            {
                response.InfoLines.Add(content);
            }
            else
            {
                textBytes += endIdx - contentStart;
                if (textBytes > MaxTextBytes)
                {
                    response.State = FastbootResponseState.Fail;
                    response.Info = "device sent too much TEXT payload";
                    response.Text = textBuffer?.ToString() ?? string.Empty;
                    return available;
                }

                textBuffer ??= new StringBuilder();
                textBuffer.Append(content);
            }

            response.State = isInfo ? FastbootResponseState.Info : FastbootResponseState.Text;
            return SkipFramePadding(pending, endIdx) - offset;
        }

        var dataEnd = FindDataEnd(pending, offset + 4);
        if (dataEnd < 0)
        {
            return 0;
        }

        var dataHex = DecodeUtf8(pending, offset + 4, dataEnd).Trim();
        var dataSize = ParseDataSizeHex(dataHex);
        if (dataSize < 0)
        {
            response.State = FastbootResponseState.Fail;
            response.Info = dataHex;
            response.Text = dataSize == -2
                ? $"data size too large {dataHex}"
                : "data size malformed: " + dataHex;
            return available;
        }

        response.State = FastbootResponseState.Data;
        response.DataSize = dataSize;
        response.ReceivedTerminalFrame = true;
        response.Text = textBuffer?.ToString() ?? string.Empty;
        return available;
    }

    /// <summary>
    /// Extracts the content of a terminal frame: everything up to the first NUL (a padded USB
    /// packet), or to the end of the buffer when no NUL follows (an exact TCP frame). Everything at
    /// and behind that NUL belongs to the same reply packet and is consumed with it.
    /// <para>提取终止帧的内容：到第一个 NUL 为止（USB 补齐包），无 NUL 时取到缓冲区末尾（TCP 精确帧）。
    /// 该 NUL 及其后的全部字节属于同一个应答包，随该帧一并消费。</para>
    /// </summary>
    private static string ExtractFrameContent(ReadOnlySpan<byte> pending, int start)
    {
        if (start >= pending.Length)
        {
            return string.Empty;
        }

        var nulIdx = pending[start..].IndexOf((byte)'\0');
        if (nulIdx >= 0)
        {
            var absolute = start + nulIdx;
            return absolute > start ? DecodeUtf8(pending, start, absolute) : string.Empty;
        }

        return DecodeUtf8(pending, start, pending.Length);
    }

    /// <summary>
    /// Advances past the NUL/CR/LF padding that belongs to the frame just parsed, stopping at the
    /// first byte that is not padding. Stopping there keeps the first byte of the next response in
    /// the pending buffer, so consecutive frames that carry no padding (an exact TCP stream) are
    /// never mistaken for padding and swallowed.
    /// <para>越过刚解析帧所属的 NUL/CR/LF 填充，并在第一个非填充字节处停止。停在该处可把下一条
    /// 响应的首字节留在待解析缓冲区中，因此不带填充的连续帧（精确的 TCP 流）绝不会被当作填充吞掉。</para>
    /// </summary>
    /// <param name="pending">Session-held pending byte buffer. 会话持有的待解析字节缓冲区。</param>
    /// <param name="position">First byte after the parsed frame content. 已解析帧内容之后的第一个字节。</param>
    /// <returns>The first position at or after <paramref name="position"/> that is not padding.
    /// <para>从 <paramref name="position"/> 起首个不是填充字节的位置。</para></returns>
    private static int SkipFramePadding(ReadOnlySpan<byte> pending, int position)
    {
        var start = position;
        while (position < pending.Length && ResponseDelimiters.Contains(pending[position]))
        {
            position++;
        }

        if (position < pending.Length && !IsKnownPrefixAt(pending, position))
        {
            return start;
        }

        return position;
    }

    /// <summary>
    /// Finds the end of the DATA size field. The DATA prefix is followed by exactly 8 hex digits
    /// (optionally NUL-padded to the packet), so the field ends at the first NUL/CR/LF within the
    /// next 8 bytes, or exactly 8 digits once that many bytes are buffered; anything shorter means
    /// more bytes are still required.
    /// <para>查找 DATA 尺寸字段结尾。DATA 前缀后固定写 8 个十六进制数字（可按包补 NUL），
    /// 因此字段结束于随后 8 字节内的首个 NUL/CR/LF，或凑满 8 个数字时恰为 8 字节处；
    /// 更短则说明还需要更多字节。</para>
    /// </summary>
    private static int FindDataEnd(ReadOnlySpan<byte> pending, int start)
    {
        var limit = Math.Min(pending.Length, start + 8);
        var stop = pending[start..limit].IndexOfAny(ResponseDelimiters);
        if (stop >= 0)
        {
            return start + stop;
        }

        return pending.Length >= start + 8 ? start + 8 : -1;
    }

    /// <summary>
    /// Finds the end of an INFO/TEXT payload: the first NUL/CR/LF delimiter. When the frame carries
    /// no delimiter at all (a TCP frame without padding), the next status prefix terminates it;
    /// until one of the two is observed more bytes are required.
    /// <para>查找 INFO/TEXT 负载的结尾：第一个 NUL/CR/LF 分隔符。帧完全无分隔符时（无填充的
    /// TCP 帧），下一个状态前缀即为其结尾；两者都未出现前需要更多字节。</para>
    /// </summary>
    private static int FindInfoTextEnd(ReadOnlySpan<byte> pending, int contentStart)
    {
        var delimiterIdx = pending[contentStart..].IndexOfAny(ResponseDelimiters);
        if (delimiterIdx >= 0)
        {
            return contentStart + delimiterIdx;
        }

        for (var i = contentStart + 1; i <= pending.Length - 4; i++)
        {
            if (IsTerminalPrefixAt(pending, i) || IsPrefixAt(pending, i, 'I', 'N', 'F', 'O') || IsPrefixAt(pending, i, 'T', 'E', 'X', 'T'))
            {
                return i;
            }
        }

        return -1;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> pending, int start, int end)
    {
        if (end <= start)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(pending[start..end]);
    }

    /// <summary>
    /// Parses the DATA frame hex size field. The field must be at most 8 hex digits and
    /// must not exceed <see cref="FastbootConstants.SparseMaxDownloadSize"/>.
    /// <para>解析 DATA 帧的 16 进制尺寸字段。该字段最多 8 个 16 进制数字，
    /// 且不得超过 <see cref="FastbootConstants.SparseMaxDownloadSize"/>。</para>
    /// </summary>
    /// <returns>The parsed size, or -1 when malformed, or -2 when too large.
    /// <para>解析出的尺寸；格式错误返回 -1，过大返回 -2。</para></returns>
    private static long ParseDataSizeHex(string dataHex)
    {
        if (dataHex.Length is 0 or > 8)
        {
            return -1;
        }

        for (var i = 0; i < dataHex.Length; i++)
        {
            if (!Uri.IsHexDigit(dataHex[i]))
            {
                return -1;
            }
        }

        return !long.TryParse(dataHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size)
            ? -1
            : size > FastbootConstants.SparseMaxDownloadSize ? -2 : size;
    }

    private static bool IsPrefixAt(ReadOnlySpan<byte> s, int index, char a, char b, char c, char d)
        => index + 4 <= s.Length &&
           s[index] == a &&
           s[index + 1] == b &&
           s[index + 2] == c &&
           s[index + 3] == d;

    private static bool IsKnownPrefixAt(ReadOnlySpan<byte> s, int index)
        => IsPrefixAt(s, index, 'O', 'K', 'A', 'Y') ||
           IsPrefixAt(s, index, 'F', 'A', 'I', 'L') ||
           IsPrefixAt(s, index, 'I', 'N', 'F', 'O') ||
           IsPrefixAt(s, index, 'T', 'E', 'X', 'T') ||
           IsPrefixAt(s, index, 'D', 'A', 'T', 'A');

    private static bool IsTerminalPrefixAt(ReadOnlySpan<byte> s, int index)
        => IsPrefixAt(s, index, 'O', 'K', 'A', 'Y') ||
           IsPrefixAt(s, index, 'F', 'A', 'I', 'L') ||
           IsPrefixAt(s, index, 'D', 'A', 'T', 'A');

}