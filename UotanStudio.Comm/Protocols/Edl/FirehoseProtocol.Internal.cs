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
    /// Conservative throughput used to size the budget for moving the remainder of a cancelled raw transfer, plus the
    /// floor and ceiling that keep a final XML acknowledgement possible and a stalled target bounded. The final XML
    /// acknowledgement is read with its own short budget.
    /// <para>按保守吞吐率为取消后原始传输剩余部分的收尾估时的下界与上界，使最后的 XML 确认仍有时间到达、
    /// 卡住的目标也不会无限拖住调用方；最后的 XML 确认使用独立短预算。</para>
    /// </summary>
    private const long QuiesceThroughputBytesPerSecond = 2 * 1024 * 1024;
    private const int MinimumQuiesceBudgetMs = 30_000;
    private const int MaximumQuiesceBudgetMs = 5 * 60 * 1000;
    private const int CancelledCompletionTimeoutMs = 15_000;

    /// <summary>
    /// Keeps the endpoint reset idempotent when both the quiesce and the outer command handler observe the same
    /// failed cancellation.
    /// <para>当收尾与外层命令处理都观测到同一次失败的取消时，保证端点只复位一次。</para>
    /// </summary>
    private bool _cancelledTransferInvalidated;

    /// <summary>
    /// Bounds the time spent returning a cancelled raw transfer to the command boundary by the bytes still owed.
    /// <para>按仍需搬运的字节数为取消后回到命令边界的收尾设界。</para>
    /// </summary>
    /// <param name="remainingBytes">Bytes the target still has to send or receive.<para>目标仍需发送或接收的字节数。</para></param>
    /// <returns>The budget in milliseconds.<para>预算（毫秒）。</para></returns>
    private static int QuiesceBudgetMs(long remainingBytes)
    {
        var estimated = remainingBytes <= 0
            ? (double)MinimumQuiesceBudgetMs
            : (double)remainingBytes / QuiesceThroughputBytesPerSecond * 1000;
        return (int)Math.Clamp(estimated, MinimumQuiesceBudgetMs, MaximumQuiesceBudgetMs);
    }

    /// <summary>
    /// Drains the bytes a Firehose target is still sending after a cancelled read, then consumes its final XML
    /// acknowledgement, so the target returns to command mode instead of blocking the endpoint in raw mode.
    /// <para>排空取消读取后目标仍在发送的字节，并消费其最后的 XML 确认，使目标回到命令模式，而不是停在原始模式
    /// 堵住端点。</para>
    /// </summary>
    /// <param name="remainingBytes">Declared payload bytes not yet consumed.<para>尚未消费的声明负载字节数。</para></param>
    /// <param name="chunkBytes">Width of one transport read, matched to the negotiated payload size.
    /// <para>单次传输读取宽度，与协商负载大小一致。</para></param>
    /// <returns>The outcome of the quiesce.<para>收尾结果。</para></returns>
    private async Task<RawPayloadOutcome> QuiesceCancelledRawReadAsync(long remainingBytes, int chunkBytes)
    {
        var deadline = Environment.TickCount64 + QuiesceBudgetMs(remainingBytes);
        var buffer = new byte[chunkBytes];
        var remaining = remainingBytes;
        var silentReads = 0;
        try
        {
            while (remaining > 0 && Environment.TickCount64 < deadline)
            {
                if (_rxBufferBytes > 0)
                {
                    var buffered = (int)Math.Min(_rxBufferBytes, remaining);
                    ShiftRxBufferLeft(buffered);
                    remaining -= buffered;
                    continue;
                }

                var toRead = (int)Math.Min(buffer.Length, remaining);
                var sliceMs = (int)Math.Min(_cmdTimeoutMs, Math.Max(1, deadline - Environment.TickCount64));
                int received;
                try
                {
                    received = await _transport.ReadAsync(buffer, 0, toRead, sliceMs, CancellationToken.None)
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
                remaining -= received;
            }

            if (remaining > 0 || !await ConsumeCancelledCompletionAsync().ConfigureAwait(false))
            {
                InvalidateCancelledTransfer("the target did not finish the declared payload and its completion");
                return RawPayloadOutcome.Unrecoverable;
            }

            Log($"Cancelled raw read drained {remainingBytes} byte(s) and consumed the target completion; " +
                "the Firehose link stays in command mode.");
            return RawPayloadOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            InvalidateCancelledTransfer(ex.Message);
            return RawPayloadOutcome.Unrecoverable;
        }
    }

    /// <summary>
    /// Zero-fills the bytes a Firehose target is still waiting for after a cancelled program, then consumes its final
    /// XML acknowledgement, so the device leaves the raw data phase instead of waiting on a closed handle.
    /// <para>为取消写入后目标仍在等待的字节补零，并消费其最后的 XML 确认，使设备退出原始数据阶段，而不是
    /// 等着一个已关闭的句柄。</para>
    /// </summary>
    /// <param name="remainingBytes">Declared payload bytes not yet written.<para>尚未写入的声明负载字节数。</para></param>
    /// <param name="payloadSize">Width of one negotiated payload block.<para>单个协商负载块的宽度。</para></param>
    /// <returns>The outcome of the quiesce.<para>收尾结果。</para></returns>
    private async Task<RawPayloadOutcome> QuiesceCancelledRawWriteAsync(long remainingBytes, int payloadSize)
    {
        var deadline = Environment.TickCount64 + QuiesceBudgetMs(remainingBytes);
        var buffer = new byte[payloadSize];
        var remaining = remainingBytes;
        var zlpTimeout = GetZlpTimeoutMs();
        try
        {
            while (remaining > 0 && Environment.TickCount64 < deadline)
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
            }

            if (remaining > 0 || !await ConsumeCancelledCompletionAsync().ConfigureAwait(false))
            {
                InvalidateCancelledTransfer("the target did not accept the remaining payload and acknowledge completion");
                return RawPayloadOutcome.Unrecoverable;
            }

            Log($"Cancelled raw write zero-filled {remainingBytes} byte(s) and consumed the target completion; " +
                "the Firehose link stays in command mode.");
            return RawPayloadOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            InvalidateCancelledTransfer(ex.Message);
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
    /// Marks a cancelled transfer that could not be quiesced as unusable, drops retained response bytes and resets
    /// the endpoint, so stale raw payload can neither be attributed to the next command nor block the port.
    /// <para>把无法收尾的取消传输标记为不可用，丢弃残留响应字节并复位端点，使过期的原始负载既不会被算到
    /// 下一条命令头上，也不会堵住端口。</para>
    /// </summary>
    /// <param name="reason">Why the quiesce failed, for the diagnostic log.<para>收尾失败原因，用于诊断日志。</para></param>
    private void InvalidateCancelledTransfer(string reason)
    {
        if (_cancelledTransferInvalidated)
        {
            return;
        }

        _cancelledTransferInvalidated = true;
        _faulted = true;
        DiscardPendingResponseBytes();
        try
        {
            _transport.Reset();
        }
        catch (Exception ex)
        {
            Log($"Best-effort transport reset after a cancelled raw transfer failed: {ex.Message}");
        }

        Log($"The cancelled raw transfer could not be returned to the Firehose command boundary ({reason}); " +
            "the endpoint was reset and the target must be reconnected before another operation.");
    }
}