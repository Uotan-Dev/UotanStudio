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
        // In-memory variant backed by the streaming path; for large partitions use
        // ReadToStreamAsync to write straight to disk.
        // 内存版实现走流式路径；大分区请使用 ReadToStreamAsync 直接落盘。
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

        // (1) Send <read> and wait for the command response: the device must ACK (and normally
        // enters raw mode) before any raw payload is read.
        // (1) 发送 <read> 并等待命令响应：设备必须 ACK（通常进入 raw 模式）后才能读取
        // 原始数据。
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

        // Total budget: the response timeout plus 1 ms per KB of payload (a safety net that
        // keeps large multi-GB reads viable while never looping forever on a dead device).
        // 总预算：响应超时 + 每 KB 负载 1 毫秒（安全上限：既保证数 GB 大读取可行，又不会
        // 在设备失联时无限循环）。
        var totalBudgetMs = ((long)Configuration.ResponseTimeoutSeconds * 1000) + (totalBytes / 1024);
        var start = Environment.TickCount64;

        while (bytesRead < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((Environment.TickCount64 - start) > totalBudgetMs)
            {
                throw new EdlException(EdlErrorCode.ReadFailed,
                    $"Raw payload read exceeded total budget ({totalBudgetMs} ms) after {bytesRead}/{totalBytes} bytes.");
            }

            byte[] chunk;
            if (_rxBufferBytes > 0)
            {
                // Consume the carry-over buffer first (it may already hold raw payload read
                // past the command response).
                // 先消费结转缓冲（其中可能已包含越过命令响应读到的原始数据）。
                var fromRx = Math.Min(_rxBufferBytes, (int)Math.Min(totalBytes - bytesRead, int.MaxValue));
                chunk = new byte[fromRx];
                Array.Copy(_rxBuffer, 0, chunk, 0, fromRx);
                ShiftRxBufferLeft(fromRx);
            }
            else
            {
                var toRead = (int)Math.Min(EdlConstants.RawReadChunkBytes, totalBytes - bytesRead);
                chunk = await ReadChunkAsync(toRead, _cmdTimeoutMs, cancellationToken);
                if (chunk.Length == 0)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout,
                        $"Raw payload read timed out after {bytesRead}/{totalBytes} bytes.");
                }
            }

            // (2) Strip embedded XML log blocks: some UFS programmers emit
            // <data><log/></data> inside the raw stream — without stripping, the XML would
            // land in the partition data.
            // (2) 剥离嵌入式 XML 日志块：部分 UFS programmer 会在原始流中输出
            // <data><log/></data>——不剥离的话 XML 会落入分区数据。
            var remaining = StripEmbeddedXmlLogs(chunk, pendingXml);
            if (remaining.Length > 0)
            {
                await destination.WriteAsync(remaining, 0, remaining.Length, cancellationToken);
                bytesRead += remaining.Length;
            }
        }

        // (3) Consume the trailing <response value="ACK"/> block so the stale ACK does not
        // desync the next command.
        // (3) 消费收尾的 <response value="ACK"/> 块，避免残留 ACK 使下一条命令失步。
        await DrainTrailingResponseXmlAsync(cancellationToken);
    }

    /// <summary>
    /// Strips embedded XML log blocks from a raw-data chunk. The accumulation buffer stitches
    /// a possibly incomplete XML prefix across chunk boundaries; once the prefix is confirmed
    /// not to be XML, everything is returned as raw data.
    /// <para>从原始数据块中剥离嵌入式 XML 日志块。累积缓冲跨块拼接可能不完整的 XML 前缀；
    /// 一旦确认前缀不是 XML，全部作为原始数据返回。</para>
    /// </summary>
    /// <param name="chunk">The raw chunk. 原始数据块。</param>
    /// <param name="pendingXml">The cross-chunk XML accumulation buffer. 跨块 XML 累积缓冲。</param>
    /// <returns>The remaining raw bytes. 剥离后的有效原始字节。</returns>
    private byte[] StripEmbeddedXmlLogs(byte[] chunk, List<byte> pendingXml)
    {
        // Fast path: no pending XML prefix and this chunk does not start with "<?xml".
        // 快速路径：无待定 XML 前缀且本块不以 "<?xml" 开头。
        if (pendingXml.Count == 0 && !LooksLikeXmlHeader(chunk, chunk.Length))
        {
            return chunk;
        }

        pendingXml.AddRange(chunk);

        // While the accumulated buffer starts with "<?xml", locate and skip complete log
        // blocks.
        // 只要累积缓冲以 "<?xml" 开头，就定位并跳过完整的日志块。
        while (pendingXml.Count >= 5 &&
               pendingXml[0] == (byte)'<' && pendingXml[1] == (byte)'?' &&
               pendingXml[2] == (byte)'x' && pendingXml[3] == (byte)'m' && pendingXml[4] == (byte)'l')
        {
            var text = Encoding.UTF8.GetString([.. pendingXml]);
            var xmlSize = FindCompleteXml(text);
            if (xmlSize <= 0)
            {
                // The XML block is incomplete; keep accumulating.
                // XML 块尚未收全；继续累积。
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

        // Not an XML prefix: the accumulated buffer is all valid raw data.
        // 非 XML 前缀：累积缓冲全部是有效原始数据。
        if (pendingXml.Count == 0)
        {
            return [];
        }

        var result = pendingXml.ToArray();
        pendingXml.Clear();
        return result;
    }
}