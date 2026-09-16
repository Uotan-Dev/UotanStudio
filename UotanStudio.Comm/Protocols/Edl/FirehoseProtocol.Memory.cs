using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Firehose protocol read and memory (peek/poke/memcpy) operations.
/// <para>Firehose 协议的读取与内存（peek/poke/memcpy）操作。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Reads data from a partition into memory.
    /// <para>从分区读取数据到内存。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A tuple containing the response and data. 包含响应和数据的元组。</returns>
    public async Task<(FirehoseResponse Response, byte[] Data)> ReadAsync(long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        var response = await ReadToStreamAsync(ms, startSector, numSectors, filename, physicalPartition, cancellationToken);
        return (response, ms.ToArray());
    }

    /// <summary>
    /// Streams raw partition data straight into the specified destination stream without
    /// allocating a full-size in-memory buffer.
    /// <para>将分区原始数据流式写入指定目标流，不整块分配等大小的内存缓冲。</para>
    /// </summary>
    /// <param name="destination">Destination stream for the raw data. 原始数据写入目标流。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The command response (raw payload excluded). 命令响应（不含原始数据）。</returns>
    public async Task<FirehoseResponse> ReadToStreamAsync(Stream destination, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        var xml = FirehoseXmlBuilder.BuildRead(Configuration.SectorSize, numSectors, filename, physicalPartition, startSector, slot: EffectiveSlot);

        var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken);
        if (response.IsRawMode)
        {
            Log("Device entered raw mode for read, receiving raw data...");
        }

        if (!response.IsAck)
        {
            Log($"Read command returned {response.RawValue} for {filename}.");
            return response;
        }

        var totalBytes = numSectors * Configuration.SectorSize;
        await ReadRawPayloadToStreamAsync(destination, totalBytes, cancellationToken);
        return response;
    }

    /// <summary>
    /// Reads memory from the target device via &lt;peek&gt;.
    /// <para>通过 &lt;peek&gt; 从目标设备读取内存。</para>
    /// </summary>
    /// <param name="sizeInBytes">The number of bytes to read. 待读取字节数。</param>
    /// <param name="address">The memory address. 内存地址。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A tuple containing the response and the read data. 包含响应与读取数据的元组。</returns>
    public async Task<(FirehoseResponse Response, byte[] Data)> PeekAsync(long sizeInBytes, long address, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var addressStr = $"0x{address:X}";
        var xml = FirehoseXmlBuilder.BuildPeek(sizeInBytes, addressStr);
        Log($"Sent <peek> address={addressStr}, size={sizeInBytes}");

        var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken);
        if (!response.IsAck)
        {
            throw new EdlException(EdlErrorCode.PeekFailed,
                $"Peek failed at address {addressStr}. Response: {response.RawValue}");
        }

        using var ms = new MemoryStream();
        await ReadRawPayloadToStreamAsync(ms, sizeInBytes, cancellationToken);
        return (response, ms.ToArray());
    }

    /// <summary>
    /// Writes memory on the target device via &lt;poke&gt;.
    /// <para>通过 &lt;poke&gt; 向目标设备写入内存。</para>
    /// </summary>
    /// <param name="sizeInBytes">The number of bytes to write. 待写入字节数。</param>
    /// <param name="address">The memory address. 内存地址。</param>
    /// <param name="value">The value bytes. 值字节。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> PokeAsync(long sizeInBytes, long address, byte[] value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (value.Length != sizeInBytes)
        {
            throw new ArgumentException($"Value length ({value.Length}) does not match sizeInBytes ({sizeInBytes}).", nameof(value));
        }

        var addressStr = $"0x{address:X}";
        var valueStr = $"0x{BitConverter.ToString(value).Replace("-", "")}";
        var xml = FirehoseXmlBuilder.BuildPoke(sizeInBytes, addressStr, valueStr);
        Log($"Sent <poke> address={addressStr}, size={sizeInBytes}");

        var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken);
        return !response.IsAck
            ? throw new EdlException(EdlErrorCode.PokeFailed,
                $"Poke failed at address {addressStr}. Response: {response.RawValue}")
            : response;
    }

    /// <summary>
    /// Writes a 32-bit word to the target device via &lt;pokedword&gt;.
    /// <para>通过 &lt;pokedword&gt; 向目标设备写入 32 位字。</para>
    /// </summary>
    /// <param name="address">The target address. 目标地址。</param>
    /// <param name="value">The 32-bit value. 32 位值。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> PokeDwordAsync(long address, uint value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var addressStr = $"0x{address:X}";
        var valueStr = $"0x{value:X8}";
        var xml = FirehoseXmlBuilder.BuildPokeDword(addressStr, valueStr);
        Log($"Sent <pokedword> address={addressStr}, value={valueStr}");

        var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken);
        return !response.IsAck
            ? throw new EdlException(EdlErrorCode.PokeFailed,
                $"PokeDword failed at address {addressStr}. Response: {response.RawValue}")
            : response;
    }

    /// <summary>
    /// Performs a memory copy on the target device via &lt;memcpy&gt;.
    /// <para>通过 &lt;memcpy&gt; 在目标设备执行内存拷贝。</para>
    /// </summary>
    /// <param name="dstOffset">The destination address. 目标地址。</param>
    /// <param name="srcOffset">The source address. 源地址。</param>
    /// <param name="size">The copy size in bytes. 拷贝大小（字节）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> MemcpyAsync(long dstOffset, long srcOffset, long size, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var dstStr = $"0x{dstOffset:X}";
        var srcStr = $"0x{srcOffset:X}";
        var xml = FirehoseXmlBuilder.BuildMemcpy(dstStr, srcStr, size);
        Log($"Sent <memcpy> dst={dstStr}, src={srcStr}, size={size}");

        var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken);
        return !response.IsAck
            ? throw new EdlException(EdlErrorCode.MemoryCommandFailed,
                $"Memcpy failed: dst={dstStr}, src={srcStr}, size={size}. Response: {response.RawValue}")
            : response;
    }

    /// <summary>
    /// Streams the specified number of raw payload bytes into the destination stream, stripping
    /// embedded XML log blocks that some programmers emit inside the raw stream, then consumes
    /// the trailing &lt;response value="ACK"/&gt; block.
    /// <para>将指定字节数的原始数据流式写入目标流，剥离部分 programmer 在原始流中嵌入的
    /// XML 日志块，随后消费收尾的 &lt;response value="ACK"/&gt; 块。</para>
    /// </summary>
    /// <param name="destination">The destination stream. 目标流。</param>
    /// <param name="totalBytes">The number of raw bytes to read. 待读取的原始字节数。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task ReadRawPayloadToStreamAsync(Stream destination, long totalBytes, CancellationToken cancellationToken)
    {
        long bytesRead = 0;
        var pendingXml = new List<byte>(EdlConstants.ResponseReadChunkBytes);

        var totalBudgetMs = ((long)Configuration.ResponseTimeoutSeconds * 1000) + (totalBytes / 1024);
        var start = Environment.TickCount64;

        var rawBuffer = new byte[EdlConstants.RawReadChunkBytes];

        while (bytesRead < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((Environment.TickCount64 - start) > totalBudgetMs)
            {
                throw new EdlException(EdlErrorCode.ReadFailed,
                    $"Raw payload read exceeded total budget ({totalBudgetMs} ms) after {bytesRead}/{totalBytes} bytes.");
            }

            byte[] chunkBuffer;
            int chunkLength;
            if (_rxBufferBytes > 0)
            {
                var fromRx = Math.Min(_rxBufferBytes, (int)Math.Min(totalBytes - bytesRead, int.MaxValue));
                chunkBuffer = _rxBuffer;
                chunkLength = fromRx;
            }
            else
            {
                var toRead = (int)Math.Min(EdlConstants.RawReadChunkBytes, totalBytes - bytesRead);
                var n = await _transport.ReadAsync(rawBuffer, 0, toRead, _cmdTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout,
                        $"Raw payload read timed out after {bytesRead}/{totalBytes} bytes.");
                }

                chunkBuffer = rawBuffer;
                chunkLength = n;
            }

            var remaining = StripEmbeddedXmlLogs(chunkBuffer, chunkLength, pendingXml);
            if (remaining.Length > 0)
            {
                await destination.WriteAsync(remaining, 0, remaining.Length, cancellationToken);
                bytesRead += remaining.Length;
            }

            if (chunkBuffer == _rxBuffer)
            {
                ShiftRxBufferLeft(chunkLength);
            }
        }

        await DrainTrailingResponseXmlAsync(cancellationToken);
    }

    /// <summary>
    /// Strips embedded XML log blocks from a raw-data chunk. The accumulation buffer stitches
    /// a possibly incomplete XML prefix across chunk boundaries; once the prefix is confirmed
    /// not to be XML, everything is returned as raw data.
    /// <para>从原始数据块中剥离嵌入式 XML 日志块。累积缓冲跨块拼接可能不完整的 XML 前缀；
    /// 一旦确认前缀不是 XML，全部作为原始数据返回。</para>
    /// </summary>
    /// <param name="chunk">The raw chunk buffer. 原始数据缓冲。</param>
    /// <param name="chunkLength">The valid byte count in the buffer. 缓冲中的有效字节数。</param>
    /// <param name="pendingXml">The cross-chunk XML accumulation buffer. 跨块 XML 累积缓冲。</param>
    /// <returns>The remaining raw bytes. 剥离后的有效原始字节。</returns>
    private byte[] StripEmbeddedXmlLogs(byte[] chunk, int chunkLength, List<byte> pendingXml)
    {
        if (pendingXml.Count == 0 && !LooksLikeXmlHeader(chunk, chunkLength))
        {
            return chunk.AsSpan(0, chunkLength).ToArray();
        }

        pendingXml.AddRange(chunk.AsSpan(0, chunkLength).ToArray());

        while (pendingXml.Count >= 5 &&
               pendingXml[0] == (byte)'<' && pendingXml[1] == (byte)'?' &&
               pendingXml[2] == (byte)'x' && pendingXml[3] == (byte)'m' && pendingXml[4] == (byte)'l')
        {
            var text = Encoding.UTF8.GetString([.. pendingXml]);
            var xmlSize = FindCompleteXml(text);
            if (xmlSize <= 0)
            {
                return [];
            }

            var completeXml = text[..xmlSize];
            Log($"Skipping embedded XML block in raw data ({xmlSize} bytes).");
            try
            {
                var block = FirehoseResponseParser.ParseDataBlock(completeXml);
                foreach (var logValue in block.Logs)
                {
                    OnTargetLog(logValue);
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to parse embedded XML block in raw data: {ex.Message}");
            }
            pendingXml.RemoveRange(0, xmlSize);
        }

        if (pendingXml.Count == 0)
        {
            return [];
        }

        var result = pendingXml.ToArray();
        pendingXml.Clear();
        return result;
    }
}