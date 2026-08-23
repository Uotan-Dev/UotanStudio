using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Firehose protocol response reception and transport hygiene helpers.
/// <para>Firehose 协议响应接收与传输清理辅助。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Receives the next Firehose response containing a <c>&lt;response&gt;</c> element.
    /// <para>接收下一个包含 <c>&lt;response&gt;</c> 元素的 Firehose 响应。</para>
    /// </summary>
    /// <remarks>
    /// Blocks containing logs but no <c>&lt;response&gt;</c> are consumed (their logs are
    /// surfaced via <see cref="TargetLogReceived"/>) and the timeout timer is reset — only a
    /// block containing a <c>&lt;response&gt;</c> is returned. A single block may mix logs and
    /// the response. Bytes following the returned block are carried over in the internal RX
    /// buffer for the next command.
    /// <para>仅含日志而无 <c>&lt;response&gt;</c> 的块会被消费（日志经
    /// <see cref="TargetLogReceived"/> 上报）并重置超时计时器——只有包含
    /// <c>&lt;response&gt;</c> 的块才会返回。单个块内可能混有日志与 response。返回块之后
    /// 的字节会保留在内部接收缓冲中，供下一条命令使用。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds. 可选的超时（毫秒）。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    /// <exception cref="EdlException">Thrown when no response arrives within the timeout. 超时未收到响应时抛出。</exception>
    private async Task<FirehoseResponse> ReceiveResponseAsync(CancellationToken cancellationToken, int? timeoutMs = null)
    {
        ThrowIfDisposed();
        var start = Environment.TickCount64;
        var effectiveTimeoutMs = timeoutMs ?? ((long)Configuration.ResponseTimeoutSeconds * 1000);
        var responseBuffer = new byte[EffectiveMaxPayloadFromTarget];
        var responseBufferPos = 0;

        while ((Environment.TickCount64 - start) < effectiveTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Move any carry-over bytes (from the previous command's read) into the response
            // buffer first — only newly-read bytes get appended, never the whole buffer again.
            // 先把（上一条命令读取遗留的）结转字节移入响应缓冲——只追加新读取的字节，
            // 绝不重复追加整个缓冲。
            if (_rxBufferBytes > 0)
            {
                var copied = Math.Min(_rxBufferBytes, responseBuffer.Length - responseBufferPos);
                Array.Copy(_rxBuffer, 0, responseBuffer, responseBufferPos, copied);
                responseBufferPos += copied;
                ShiftRxBufferLeft(copied);
            }

            if (responseBufferPos > 0)
            {
                // Normalize the buffer so it starts at the "<?xml" marker, discarding any
                // unexpected bytes before it. A buffer that is merely an incomplete prefix of
                // "<?xml" is kept waiting for more data.
                // 规整缓冲，使其从 "<?xml" 标记开始，丢弃之前的意外字节。仅是 "<?xml" 的
                // 不完整前缀的缓冲保留，等待更多数据。
                if (!LooksLikeXmlHeader(responseBuffer, responseBufferPos))
                {
                    var xmlStart = FindXmlMarker(responseBuffer, responseBufferPos);
                    if (xmlStart > 0)
                    {
                        Log($"Discarding {xmlStart} unexpected bytes before XML.");
                        Array.Copy(responseBuffer, xmlStart, responseBuffer, 0, responseBufferPos - xmlStart);
                        responseBufferPos -= xmlStart;
                    }
                    else if (!IsPartialXmlMarkerPrefix(responseBuffer, responseBufferPos))
                    {
                        // No XML marker present: garbage data; clear and wait for more.
                        // 缓冲中没有 "<?xml" 标记：垃圾数据；清空等待更多数据。
                        Log($"Discarding {responseBufferPos} unexpected bytes (no XML marker).");
                        responseBufferPos = 0;
                    }
                }

                if (responseBufferPos >= 5 && LooksLikeXmlHeader(responseBuffer, responseBufferPos))
                {
                    var xmlStr = Encoding.UTF8.GetString(responseBuffer, 0, responseBufferPos);
                    var xmlSize = FindCompleteXml(xmlStr);
                    if (xmlSize > 0)
                    {
                        var completeXml = xmlStr[..xmlSize];
                        var block = FirehoseResponseParser.ParseDataBlock(completeXml);

                        if (block.Logs.Count > 0 || block.Response != null)
                        {
                            foreach (var logValue in block.Logs)
                            {
                                OnTargetLog(logValue);
                            }

                            var blockBytes = Encoding.UTF8.GetByteCount(completeXml);

                            if (block.Response != null)
                            {
                                // Keep the bytes AFTER the returned block in the carry-over
                                // buffer for the next command (fixes the old bug that
                                // discarded the post-block remainder).
                                // 将返回块之后的字节保留到结转缓冲，供下一条命令使用
                                // （修复旧实现丢弃块后剩余字节的缺陷）。
                                if (blockBytes < responseBufferPos)
                                {
                                    var remaining = responseBufferPos - blockBytes;
                                    Array.Copy(responseBuffer, blockBytes, _rxBuffer, 0, remaining);
                                    _rxBufferBytes = remaining;
                                }

                                return block.Response;
                            }

                            // Log-only block: consume it and keep reading; reset the timer.
                            // 仅日志块：消费并继续读取；重置计时器。
                            if (blockBytes <= responseBufferPos)
                            {
                                Array.Copy(responseBuffer, blockBytes, responseBuffer, 0, responseBufferPos - blockBytes);
                                responseBufferPos -= blockBytes;
                            }
                            start = Environment.TickCount64;
                            continue;
                        }

                        // Neither logs nor a response node: fall back to whole-block parsing.
                        // 既无日志也无 response 节点：回退到整块解析。
                        var response = FirehoseResponseParser.Parse(completeXml);
                        var responseBytes = Encoding.UTF8.GetByteCount(completeXml);
                        if (responseBytes < responseBufferPos)
                        {
                            var remaining = responseBufferPos - responseBytes;
                            Array.Copy(responseBuffer, responseBytes, _rxBuffer, 0, remaining);
                            _rxBufferBytes = remaining;
                        }
                        return response;
                    }
                }
            }

            // Partial read: returns whatever arrived (empty on timeout) — never blocks for the
            // full chunk size, and never consumes+discards data.
            // 部分读取：返回已到达的数据（超时返回空）——既不为凑满块大小而阻塞，也不
            // 消费后丢弃数据。
            var data = await ReadChunkAsync(
                Math.Min(EffectiveMaxPayloadFromTarget, EdlConstants.ResponseReadChunkBytes),
                _cmdTimeoutMs, cancellationToken);
            if (data.Length > 0)
            {
                var spaceInRx = _rxBuffer.Length - _rxBufferBytes;
                var toCopy = Math.Min(data.Length, spaceInRx);
                Array.Copy(data, 0, _rxBuffer, _rxBufferBytes, toCopy);
                _rxBufferBytes += toCopy;
            }
        }

        throw new EdlException(EdlErrorCode.TransportTimeout,
            $"Timeout waiting for Firehose response ({effectiveTimeoutMs / 1000.0:0.##}s).");
    }

    /// <summary>
    /// Drains any pending data from the transport (best-effort, capped at 10 reads × 100 ms).
    /// <para>排出传输中的待处理数据（尽力而为，上限 10 次读取 × 100 毫秒）。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task DrainPendingDataAsync(CancellationToken cancellationToken)
    {
        // Draining is best-effort cleanup: no exception (read timeout, port error) may abort
        // the main flow. Everything except cancellation is swallowed.
        // 排空是尽力而为的清理：任何异常（读超时、端口错误）都不应中断主流程。除取消外
        // 一律吞掉。
        try
        {
            _rxBufferBytes = 0;
            for (var i = 0; i < EdlConstants.DrainMaxReads; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = await ReadChunkAsync(EdlConstants.ResponseReadChunkBytes, EdlConstants.DrainReadTimeoutMs, cancellationToken);
                if (data.Length == 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Drain read failed (best-effort, ignored): {ex.Message}");
        }
    }

    /// <summary>
    /// Consumes the trailing XML block (<c>&lt;response value="ACK"/&gt;</c>) the device sends
    /// after a raw-data read, so the stale ACK does not desync the next command.
    /// <para>消费 raw 数据读取之后设备发送的收尾 XML 块（<c>&lt;response value="ACK"/&gt;</c>），
    /// 避免残留 ACK 使下一条命令失步。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task DrainTrailingResponseXmlAsync(CancellationToken cancellationToken)
    {
        try
        {
            var start = Environment.TickCount64;
            var budgetMs = Math.Max(_cmdTimeoutMs, 3000);
            var pending = new List<byte>(EdlConstants.ResponseReadChunkBytes);

            while ((Environment.TickCount64 - start) < budgetMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] chunk;
                if (_rxBufferBytes > 0)
                {
                    chunk = new byte[_rxBufferBytes];
                    Array.Copy(_rxBuffer, 0, chunk, 0, _rxBufferBytes);
                    _rxBufferBytes = 0;
                }
                else
                {
                    chunk = await ReadChunkAsync(EdlConstants.ResponseReadChunkBytes, 500, cancellationToken);
                    if (chunk.Length == 0)
                    {
                        break; // no more data
                    }
                }

                pending.AddRange(chunk);
                if (FindCompleteXml(Encoding.UTF8.GetString([.. pending])) > 0)
                {
                    Log($"Drained trailing response XML ({pending.Count} bytes).");
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a failed trailing drain must not abort the main flow.
            // 尽力而为：收尾消费失败不影响主流程。
            Log($"Trailing drain failed (best-effort, ignored): {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the end offset of the first complete <c>&lt;/data&gt;</c> block in the string, or
    /// 0 when no complete block is present.
    /// <para>查找字符串中首个完整 <c>&lt;/data&gt;</c> 块的结束偏移；无完整块时返回 0。</para>
    /// </summary>
    /// <param name="xml">The accumulated XML text. 累积的 XML 文本。</param>
    /// <returns>The end offset, or 0. 结束偏移，或 0。</returns>
    private static int FindCompleteXml(string xml)
    {
        var closeDataIdx = xml.IndexOf("</data>", StringComparison.Ordinal);
        return closeDataIdx < 0 ? 0 : closeDataIdx + "</data>".Length;
    }

    /// <summary>
    /// Byte-level check whether the buffer starts with <c>&amp;lt;?xml</c>.
    /// <para>字节级判断缓冲开头是否为 <c>&amp;lt;?xml</c>。</para>
    /// </summary>
    /// <param name="buffer">The buffer. 缓冲。</param>
    /// <param name="length">The valid length. 有效长度。</param>
    /// <returns><c>true</c> if the buffer starts with the XML marker. 缓冲以 XML 标记开头时返回 <c>true</c>。</returns>
    private static bool LooksLikeXmlHeader(byte[] buffer, int length)
    {
        return length >= 5 && buffer[0] == (byte)'<' && buffer[1] == (byte)'?' &&
               buffer[2] == (byte)'x' && buffer[3] == (byte)'m' && buffer[4] == (byte)'l';
    }

    /// <summary>
    /// Byte-level search for the <c>&amp;lt;?xml</c> marker offset in the buffer; -1 when absent.
    /// <para>字节级搜索 <c>&amp;lt;?xml</c> 标记在缓冲中的起始偏移；未找到返回 -1。</para>
    /// </summary>
    /// <param name="buffer">The buffer. 缓冲。</param>
    /// <param name="length">The valid length. 有效长度。</param>
    /// <returns>The marker offset, or -1. 标记偏移，或 -1。</returns>
    private static int FindXmlMarker(byte[] buffer, int length)
    {
        if (length < 5)
        {
            return -1;
        }

        for (var i = 0; i <= length - 5; i++)
        {
            if (buffer[i] == (byte)'<' && buffer[i + 1] == (byte)'?' &&
                buffer[i + 2] == (byte)'x' && buffer[i + 3] == (byte)'m' && buffer[i + 4] == (byte)'l')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Checks whether the buffer is a (possibly incomplete) prefix of the <c>&amp;lt;?xml</c>
    /// marker, meaning an XML header may be split across reads and must be kept.
    /// <para>判断缓冲是否为 <c>&amp;lt;?xml</c> 标记的（可能不完整的）前缀，即 XML 头可能
    /// 被拆分在多次读取中，必须保留。</para>
    /// </summary>
    /// <param name="buffer">The buffer. 缓冲。</param>
    /// <param name="length">The valid length. 有效长度。</param>
    /// <returns><c>true</c> if the buffer can still become an XML marker. 缓冲仍可能成为 XML 标记时返回 <c>true</c>。</returns>
    private static bool IsPartialXmlMarkerPrefix(byte[] buffer, int length)
    {
        if (length is <= 0 or >= 5)
        {
            return false;
        }

        // length is 1..4 here; check the buffer against the "<?xml" prefix byte by byte.
        // 此处 length 为 1..4；将缓冲与 "<?xml" 前缀逐字节比较。
        return (length >= 4 && buffer[0] == (byte)'<' && buffer[1] == (byte)'?' && buffer[2] == (byte)'x' && buffer[3] == (byte)'m') ||
               (length >= 3 && buffer[0] == (byte)'<' && buffer[1] == (byte)'?' && buffer[2] == (byte)'x') ||
               (length >= 2 && buffer[0] == (byte)'<' && buffer[1] == (byte)'?') ||
               (length == 1 && buffer[0] == (byte)'<');
    }

    /// <summary>
    /// Shifts the internal RX carry-over buffer left by the specified number of bytes.
    /// <para>将内部接收结转缓冲向左移动指定字节数。</para>
    /// </summary>
    /// <param name="bytes">The number of bytes consumed. 已消费的字节数。</param>
    private void ShiftRxBufferLeft(int bytes)
    {
        if (bytes <= 0 || bytes > _rxBufferBytes)
        {
            return;
        }

        var remaining = _rxBufferBytes - bytes;
        if (remaining > 0)
        {
            Array.Copy(_rxBuffer, bytes, _rxBuffer, 0, remaining);
        }

        _rxBufferBytes = remaining;
    }
}