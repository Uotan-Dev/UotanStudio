using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Bounded XML reception with byte-exact carry-over into raw transfers.
/// <para>在大小上限内接收 XML 响应，未消费的字节原样结转到后续原始传输读取。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    private const int MaximumResponseBytes = 64 * 1024;

    private async Task<FirehoseResponse> ReceiveResponseAsync(CancellationToken cancellationToken, int? timeoutMs = null,
        Action<string>? logObserver = null)
    {
        ThrowIfDisposed();
        var budget = timeoutMs ?? checked(Configuration.ResponseTimeoutSeconds * 1000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        var start = Environment.TickCount64;
        var document = new byte[MaximumResponseBytes];
        var length = 0;

        while (Environment.TickCount64 - start < budget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rxBufferBytes == 0)
            {
                var remainingMs = (int)Math.Max(1, budget - (Environment.TickCount64 - start));
                var received = await _transport.ReadAsync(_rxBuffer, 0, _rxBuffer.Length,
                    Math.Min(Math.Max(1, _cmdTimeoutMs), remainingMs), cancellationToken).ConfigureAwait(false);
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
                if (length == 0 && value is 0 or (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
                {
                    continue;
                }

                if (length == document.Length)
                {
                    throw new FirehoseException("Firehose XML exceeds the 64 KiB response limit.");
                }

                document[length++] = value;
                if (!document.AsSpan(0, length).EndsWith("</data>"u8))
                {
                    continue;
                }

                ShiftRxBufferLeft(consumed);
                var block = FirehoseResponseParser.ParseDataBlock(Encoding.UTF8.GetString(document, 0, length));
                foreach (var log in block.Logs)
                {
                    logObserver?.Invoke(log);
                    OnTargetLog(log);
                }

                if (block.Response is not null)
                {
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
}