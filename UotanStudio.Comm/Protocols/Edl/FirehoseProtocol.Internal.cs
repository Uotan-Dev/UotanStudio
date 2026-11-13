using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Bounded XML reception with byte-exact carry-over into raw transfers.
/// <para>在大小上限内接收 XML 响应，未消费的字节原样结转到后续原始传输读取。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    private const int MaximumResponseBytes = 64 * 1024;

    /// <summary>
    /// The only response terminator; matching is gated on its final byte so the scan runs once per
    /// candidate byte instead of once per appended byte.
    /// <para>唯一的响应终止符；匹配由其末字节触发，扫描只发生在候选字节上而不是每个追加字节上。</para>
    /// </summary>
    private static ReadOnlySpan<byte> ResponseTerminator => "</data>"u8;

    private async Task<FirehoseResponse> ReceiveResponseAsync(CancellationToken cancellationToken, int? timeoutMs = null,
        Action<string>? logObserver = null, Action<FirehoseDataBlock>? blockObserver = null)
    {
        ThrowIfDisposed();
        var budget = timeoutMs ?? checked(Configuration.ResponseTimeoutSeconds * 1000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        var start = Environment.TickCount64;
        var document = _responseDocument;
        var length = 0;
        var prefixPending = true;
        string? lastTargetLog = null;

        while (Environment.TickCount64 - start < budget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rxBufferBytes == 0)
            {
                var remainingMs = (int)Math.Max(1, budget - (Environment.TickCount64 - start));
                int received;
                try
                {
                    received = await _transport.ReadAsync(_rxBuffer, 0, _rxBuffer.Length,
                        Math.Min(Math.Max(1, _cmdTimeoutMs), remainingMs), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout, ex.Message, ex);
                }
                if (received < 0 || received > _rxBuffer.Length)
                {
                    throw new FirehoseException("The transport returned an invalid receive length.");
                }

                _rxBufferBytes = received;
                if (received == 0)
                {
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            var consumed = 0;
            while (consumed < _rxBufferBytes)
            {
                var value = _rxBuffer[consumed++];
                if (length == 0 && prefixPending && EdlConstants.DocumentLeadingPadding.Contains(value))
                {
                    continue;
                }

                prefixPending = false;
                if (length == document.Length)
                {
                    throw new FirehoseException("Firehose XML exceeds the 64 KiB response limit.");
                }

                document[length++] = value;
                if (value != (byte)'>'
                    || length < ResponseTerminator.Length
                    || !document.AsSpan(length - ResponseTerminator.Length).StartsWith(ResponseTerminator))
                {
                    continue;
                }

                ShiftRxBufferLeft(consumed);
                var skip = EdlConstants.MeasureDocumentPrefix(document.AsSpan(0, length));
                var block = FirehoseResponseParser.ParseDataBlock(
                    Encoding.UTF8.GetString(document, skip, length - skip),
                    capturePayloads: blockObserver is not null);
                blockObserver?.Invoke(block);
                foreach (var log in block.Logs)
                {
                    lastTargetLog = log;
                    logObserver?.Invoke(log);
                    OnTargetLog(log);
                }

                if (block.Response is not null)
                {
                    ReclassifyWithPrecedingLog(block.Response, lastTargetLog);
                    AdoptFatalResponse(block.Response);
                    return block.Response;
                }

                length = 0;
                consumed = 0;
                prefixPending = true;
            }

            ShiftRxBufferLeft(consumed);
        }

        throw new EdlException(EdlErrorCode.TransportTimeout, "Timed out waiting for a complete Firehose response.");
    }

    /// <summary>
    /// Discards bytes retained from an incomplete response so a bounded retry starts from a clean
    /// receive buffer instead of parsing a stale fragment. Only the configure retry path uses this;
    /// every other failure faults the session.
    /// <para>丢弃未完成响应残留的字节，使有界重试从干净的接收缓冲区开始，而不是解析陈旧残片。
    /// 只有 configure 重试路径使用；其他失败都会使会话失效。</para>
    /// </summary>
    private void DiscardPendingResponseBytes() => _rxBufferBytes = 0;

    private void ShiftRxBufferLeft(int bytes)
    {
        var remaining = _rxBufferBytes - bytes;
        if (remaining > 0)
        {
            Array.Copy(_rxBuffer, bytes, _rxBuffer, 0, remaining);
        }

        _rxBufferBytes = remaining;
    }

    /// <summary>
    /// Re-classifies a rejected response with the last target log seen earlier in the same command, because a
    /// programmer may send its reason as a separate log document before the response document.
    /// <para>用同一命令中更早出现的最后一条目标日志重新分类被拒响应，因为 programmer 可能把原因作为独立日志文档
    /// 先于响应文档发出。</para>
    /// </summary>
    /// <param name="response">The response about to be returned. <para>即将返回的响应。</para></param>
    /// <param name="lastTargetLog">The last log value received for this command, or <see langword="null"/>.
    /// <para>该命令收到的最后一条日志值；没有时为 <see langword="null"/>。</para></param>
    private static void ReclassifyWithPrecedingLog(FirehoseResponse response, string? lastTargetLog)
    {
        if (lastTargetLog is null || response.IsAck || response.IsRawMode || response.IsLog)
        {
            return;
        }

        response.Error = FirehoseErrorClassifier.Classify(response, lastTargetLog) ?? response.Error;
    }

    /// <summary>
    /// Faults the session when, and only when, the evidence classifies a rejected response as
    /// unrecoverable; a digest-mismatch report is treated as unrecoverable, while a plain NAK that
    /// only renegotiates attributes is continued through, so ordinary rejections keep the existing
    /// session behaviour untouched.
    /// <para>仅当证据把某个被拒绝响应判定为不可恢复时才让会话失效：摘要不匹配视为不可恢复，而只是
    /// 重新协商属性的普通 NAK 会被继续处理，因此普通拒绝完全保持既有的会话行为。</para>
    /// </summary>
    /// <param name="response">The response about to be returned to the calling verb.
    /// <para>即将返回给上层命令的响应。</para></param>
    private void AdoptFatalResponse(FirehoseResponse response)
    {
        if (response.Error is not { IsFatal: true })
        {
            return;
        }

        _faulted = true;
        _lastFailureKind = EdlFailureKind.Rejected;
        Log($"Target reported an unrecoverable response ({response.Error}); a new device session is required.");
    }

    /// <summary>
    /// The outcome of a raw payload phase that was interrupted by cancellation.
    /// <para>被取消打断的原始负载阶段的收尾结果。</para>
    /// </summary>
    private enum RawPayloadOutcome
    {
        /// <summary>The declared payload finished normally.<para>声明的负载已正常完成。</para></summary>
        Completed,

        /// <summary>Cancellation reached the Firehose command boundary, so the link stays reusable.
        /// <para>取消已在 Firehose 命令边界收尾，链路仍可复用。</para></summary>
        Cancelled,

        /// <summary>The link could not be quiesced; the endpoint was reset and the session needs reconnecting.
        /// <para>链路无法收尾；已复位端点，会话需要重新连接。</para></summary>
        Unrecoverable
    }

    /// <summary>
    /// The final XML acknowledgement after a drained payload gets its own short budget: the payload itself is bounded
    /// by the length the target declared, while an acknowledgement that never arrives must not keep the caller waiting.
    /// <para>排空后的最后一条 XML 确认使用独立的短预算：负载本身由目标声明的长度设界，而永不到达的确认不应继续
    /// 拖住调用方。</para>
    /// </summary>
    private const int CancelledCompletionTimeoutMs = 15_000;

    /// <summary>
    /// Default silence window for a raw payload phase: how long a link that delivers nothing is tolerated before the
    /// target is treated as stopped rather than slow. This is not a cap on a transfer — a target that keeps
    /// delivering its declared payload is read to the end, however long that takes, because the bytes it still owes
    /// are the only way back to command mode. It is the initializer for <see cref="RawSilenceTimeoutMs"/>, so the
    /// payload read and the cancelled unwind share one definition of "stopped".
    /// <para>原始负载阶段的默认静默窗口：链路在多长时间内不供数就认为目标已停而不是慢。这不是传输的上界——
    /// 只要目标还在发送声明的负载就一路读到结束，无论多久，因为它欠的字节是回到命令模式的唯一途径。它是
    /// <see cref="RawSilenceTimeoutMs"/> 的初始值，使负载读取与取消收尾共用同一个“已停”的定义。</para>
    /// </summary>
    private const int QuiesceSilenceTimeoutMs = 60_000;

    /// <summary>
    /// Progress cadence for the caller's stopping indicator: one report per this many drained bytes, so a gigabyte of
    /// unwind does not flood the UI thread while the operator still sees the number move.
    /// <para>调用方“正在停止”指示的上报节奏：每排空这么多字节上报一次，使数 GB 的收尾不会淹没界面线程，操作者
    /// 又能看到数字在动。</para>
    /// </summary>
    private const long QuiesceProgressStepBytes = 1 * 1024 * 1024;

    /// <summary>
    /// How long a raw payload phase tolerates a link that delivers nothing before it treats the target as stopped
    /// rather than slow. This is not a cap on the transfer: a target that keeps delivering its declared payload is
    /// read to the end, however long that takes, because the bytes it still owes are the only way back to command
    /// mode. Only silence ends a read now that no wall clock does.
    /// <para>原始负载阶段在多长时间内收不到任何字节就认为目标已停而不是慢。这不是传输的上界：只要目标还在发送声明的
    /// 负载就一路读到结束，无论多久，因为它欠的字节是回到命令模式的唯一途径。既然不再有墙钟，只有静默能结束一次读取。</para>
    /// </summary>
    /// <remarks>
    /// Kept settable so a test can compress a minute of silence into milliseconds instead of waiting it out.
    /// <para>保持可写，使测试能把一分钟的静默压缩成毫秒，而不必真的等。</para>
    /// </remarks>
    internal int RawSilenceTimeoutMs { get; set; } = QuiesceSilenceTimeoutMs;

    /// <summary>
    /// Keeps the endpoint reset idempotent when the quiesce, a stall and the outer command handler all observe the
    /// same abandoned raw transfer.
    /// <para>当收尾、停摆与外层命令处理都观测到同一次被放弃的原始传输时，保证端点只复位一次。</para>
    /// </summary>
    private bool _transferInvalidated;

    /// <summary>
    /// Drains the bytes a Firehose target is still sending after a cancelled read, then consumes its final XML
    /// acknowledgement, so the target returns to command mode instead of blocking the endpoint in raw mode.
    /// <para>排空取消读取后目标仍在发送的字节，并消费其最后的 XML 确认，使目标回到命令模式，而不是停在原始模式
    /// 堵住端点。</para>
    /// </summary>
    /// <param name="remainingBytes">Declared payload bytes not yet consumed.<para>尚未消费的声明负载字节数。</para></param>
    /// <param name="chunkBytes">Width of one transport read, matched to the negotiated payload size.
    /// <para>单次传输读取宽度，与协商负载大小一致。</para></param>
    /// <param name="totalBytes">Whole payload the target declared, so the caller can show drained/total.
    /// <para>目标声明的完整负载，使调用方可以显示“已排空/总量”。</para></param>
    /// <param name="stoppingProgress">Optional reporter for how much of the remainder has been taken back.
    /// <para>可选的报告器，上报已收回多少剩余量。</para></param>
    /// <returns>The outcome of the quiesce.<para>收尾结果。</para></returns>
    private async Task<RawPayloadOutcome> QuiesceCancelledRawReadAsync(long remainingBytes, int chunkBytes,
        long totalBytes, IProgress<EdlQuiesceProgress>? stoppingProgress)
    {
        var buffer = new byte[chunkBytes];
        var remaining = remainingBytes;
        var silentReads = 0;
        var lastDeliveryAt = Environment.TickCount64;
        var lastReported = totalBytes - remaining;
        stoppingProgress?.Report(new EdlQuiesceProgress(lastReported, totalBytes));
        try
        {
            while (remaining > 0)
            {
                if (Environment.TickCount64 - lastDeliveryAt > RawSilenceTimeoutMs)
                {
                    Log($"The target delivered nothing for {RawSilenceTimeoutMs} ms with {remaining} byte(s) " +
                        "still owed; treating it as stopped rather than slow.");
                    break;
                }

                if (_rxBufferBytes > 0)
                {
                    var buffered = (int)Math.Min(_rxBufferBytes, remaining);
                    ShiftRxBufferLeft(buffered);
                    remaining -= buffered;
                    ReportQuiesceProgress(stoppingProgress, totalBytes, totalBytes - remaining, ref lastReported);
                    continue;
                }

                var toRead = (int)Math.Min(buffer.Length, remaining);
                int received;
                try
                {
                    received = await _transport.ReadAsync(buffer, 0, toRead, _cmdTimeoutMs, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    received = 0;
                }

                if (received < 0 || received > toRead)
                {
                    break;
                }

                if (received == 0)
                {
                    if (++silentReads > MaximumSilentRawReads)
                    {
                        break;
                    }

                    await Task.Delay(ZeroLengthReadRetryDelayMs, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                silentReads = 0;
                lastDeliveryAt = Environment.TickCount64;
                remaining -= received;
                ReportQuiesceProgress(stoppingProgress, totalBytes, totalBytes - remaining, ref lastReported);
            }

            if (remaining > 0 || !await ConsumeCancelledCompletionAsync().ConfigureAwait(false))
            {
                InvalidateTransfer("the target did not finish the declared payload and its completion");
                return RawPayloadOutcome.Unrecoverable;
            }

            Log($"Cancelled raw read drained {remainingBytes} byte(s) and consumed the target completion; " +
                "the Firehose link stays in command mode.");
            return RawPayloadOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            InvalidateTransfer(ex.Message);
            return RawPayloadOutcome.Unrecoverable;
        }
    }

    /// <summary>
    /// Reports how much of a cancelled transfer has been taken back, at most once per
    /// <see cref="QuiesceProgressStepBytes"/>, so the caller's stopping indicator moves without flooding its thread.
    /// <para>上报取消的传输已收回多少，最多每 <see cref="QuiesceProgressStepBytes"/> 一次，使调用方的“正在停止”
    /// 指示在移动而不会淹没其线程。</para>
    /// </summary>
    /// <param name="stoppingProgress">The caller's reporter; <see langword="null"/> disables reporting.
    /// <para>调用方的报告器；为 <see langword="null"/> 时不上报。</para></param>
    /// <param name="totalBytes">Whole payload the target declared.<para>目标声明的完整负载。</para></param>
    /// <param name="drainedBytes">Bytes taken back so far.<para>目前收回的字节数。</para></param>
    /// <param name="lastReported">Cumulative value of the previous report, updated in place.
    /// <para>上次上报的累计值，就地更新。</para></param>
    private static void ReportQuiesceProgress(IProgress<EdlQuiesceProgress>? stoppingProgress, long totalBytes,
        long drainedBytes, ref long lastReported)
    {
        if (stoppingProgress is null || drainedBytes - lastReported < QuiesceProgressStepBytes)
        {
            return;
        }

        lastReported = drainedBytes;
        stoppingProgress.Report(new EdlQuiesceProgress(drainedBytes, totalBytes));
    }

    /// <summary>
    /// Zero-fills the bytes a Firehose target is still waiting for after a cancelled program, then consumes its final
    /// XML acknowledgement, so the device leaves the raw data phase instead of waiting on a closed handle.
    /// <para>为取消写入后目标仍在等待的字节补零，并消费其最后的 XML 确认，使设备退出原始数据阶段，而不是
    /// 等着一个已关闭的句柄。</para>
    /// </summary>
    /// <param name="remainingBytes">Declared payload bytes not yet written.<para>尚未写入的声明负载字节数。</para></param>
    /// <param name="payloadSize">Width of one negotiated payload block.<para>单个协商负载块的宽度。</para></param>
    /// <param name="totalBytes">Whole payload the target declared, so the caller can show drained/total.
    /// <para>目标声明的完整负载，使调用方可以显示“已排空/总量”。</para></param>
    /// <param name="stoppingProgress">Optional reporter for how much of the remainder has been zero-filled.
    /// <para>可选的报告器，上报已补零多少剩余量。</para></param>
    /// <returns>The outcome of the quiesce.<para>收尾结果。</para></returns>
    private async Task<RawPayloadOutcome> QuiesceCancelledRawWriteAsync(long remainingBytes, int payloadSize,
        long totalBytes, IProgress<EdlQuiesceProgress>? stoppingProgress)
    {
        var buffer = new byte[payloadSize];
        var remaining = remainingBytes;
        var lastReported = totalBytes - remaining;
        stoppingProgress?.Report(new EdlQuiesceProgress(lastReported, totalBytes));
        var zlpTimeout = GetZlpTimeoutMs();
        try
        {
            while (remaining > 0)
            {
                var slice = (int)Math.Min(buffer.Length, remaining);
                var written = await _transport.WriteAsync(buffer, 0, slice, zlpTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
                if (written != slice)
                {
                    break;
                }

                if (ShouldTerminateBlockWithZlp(Configuration.ZlpAwareHost, slice, (int)Configuration.SectorSize)
                    && FindUsbDeviceTransport(_transport) is { } usbTransport)
                {
                    await usbTransport.Session.WriteZlpAsync(zlpTimeout, CancellationToken.None).ConfigureAwait(false);
                }

                remaining -= slice;
                ReportQuiesceProgress(stoppingProgress, totalBytes, totalBytes - remaining, ref lastReported);
            }

            if (remaining > 0 || !await ConsumeCancelledCompletionAsync().ConfigureAwait(false))
            {
                InvalidateTransfer("the target did not accept the remaining payload and acknowledge completion");
                return RawPayloadOutcome.Unrecoverable;
            }

            Log($"Cancelled raw write zero-filled {remainingBytes} byte(s) and consumed the target completion; " +
                "the Firehose link stays in command mode.");
            return RawPayloadOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            InvalidateTransfer(ex.Message);
            return RawPayloadOutcome.Unrecoverable;
        }
    }

    /// <summary>
    /// Consumes the target's post-transfer XML acknowledgement with its own bounded timeout. The caller's token is
    /// already cancelled, so the read must not be given that token.
    /// <para>用独立的有界超时消费传输后的 XML 确认；调用方令牌已取消，因此不能把它传给这次读取。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the target acknowledged outside raw mode and the session stays usable.
    /// <para>目标在非原始模式下确认且会话仍可用时为 <see langword="true"/>。</para></returns>
    private async Task<bool> ConsumeCancelledCompletionAsync()
    {
        var response = await ReceiveResponseAsync(CancellationToken.None, CancelledCompletionTimeoutMs)
            .ConfigureAwait(false);
        return response.IsAck && !response.IsRawMode && !_faulted;
    }

    /// <summary>
    /// Marks a raw transfer that could not be returned to the command boundary as unusable, drops retained response
    /// bytes and resets the endpoint, so stale raw payload can neither be attributed to the next command nor block
    /// the port. Every exit that abandons a declared payload mid-flight lands here: a cancelled unwind that never
    /// finished, and a target that stopped delivering. Leaving the session reusable after either would hand the next
    /// command whatever the target was still streaming.
    /// <para>把无法回到命令边界的原始传输标记为不可用，丢弃残留响应字节并复位端点，使过期的原始负载既不会被算到
    /// 下一条命令头上，也不会堵住端口。每一处在声明负载未完成时放弃的出口都走到这里：未能收尾的取消，以及不再
    /// 供数的目标。这两种情况之后若仍把会话当成可复用，下一条命令读到的就是目标还在发送的东西。</para>
    /// </summary>
    /// <param name="reason">Why the transfer was abandoned, for the diagnostic log.
    /// <para>放弃该传输的原因，用于诊断日志。</para></param>
    private void InvalidateTransfer(string reason)
    {
        if (_transferInvalidated)
        {
            return;
        }

        _transferInvalidated = true;
        _faulted = true;
        DiscardPendingResponseBytes();
        try
        {
            _transport.Reset();
        }
        catch (Exception ex)
        {
            Log($"Best-effort transport reset after an abandoned raw transfer failed: {ex.Message}");
        }

        Log($"The raw transfer could not be returned to the Firehose command boundary ({reason}); " +
            "the endpoint was reset and the target must be reconnected before another operation.");
    }
}