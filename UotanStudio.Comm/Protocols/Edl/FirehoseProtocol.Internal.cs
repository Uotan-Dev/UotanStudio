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
                if (length == 0 && EdlConstants.DocumentLeadingPadding.Contains(value))
                {
                    continue;
                }

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
                var block = FirehoseResponseParser.ParseDataBlock(Encoding.UTF8.GetString(document, 0, length),
                    capturePayloads: blockObserver is not null);
                blockObserver?.Invoke(block);
                foreach (var log in block.Logs)
                {
                    logObserver?.Invoke(log);
                    OnTargetLog(log);
                }

                if (block.Response is not null)
                {
                    AdoptFatalResponse(block.Response);
                    return block.Response;
                }

                length = 0;
                consumed = 0;
            }

            ShiftRxBufferLeft(consumed);
        }

        throw new EdlException(EdlErrorCode.TransportTimeout, "Timed out waiting for a complete Firehose response.");
    }

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
}