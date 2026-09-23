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
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        if (ValidateStorageRange(startSector, numSectors, physicalPartition) > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(numSectors), "Use ReadToStreamAsync for reads exceeding 16 MiB.");
        }

        using var ms = new MemoryStream();
        var response = await ReadToStreamCoreAsync(ms, startSector, numSectors, filename, physicalPartition, cancellationToken);
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
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ReadToStreamCoreAsync(destination, startSector, numSectors, filename, physicalPartition, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FirehoseResponse> ReadToStreamCoreAsync(Stream destination, long startSector, long numSectors,
        string filename, int physicalPartition, CancellationToken cancellationToken, IProgress<long>? progress = null)
    {
        ThrowIfDisposed();
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination must be writable.", nameof(destination));
        }

        var totalBytes = ValidateStorageRange(startSector, numSectors, physicalPartition);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var xml = FirehoseXmlBuilder.BuildRead(Configuration.SectorSize, numSectors, filename, physicalPartition, startSector, slot: EffectiveSlot);
            var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken);
            RequireRawMode(response, EdlErrorCode.ReadFailed);
            await ReadRawPayloadToStreamAsync(destination, totalBytes, cancellationToken, progress);
            return await ReceiveTransferCompletionAsync(EdlErrorCode.ReadFailed, cancellationToken);
        }
        catch
        {
            _faulted = true;
            throw;
        }
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
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        ArgumentOutOfRangeException.ThrowIfNegative(address);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeInBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeInBytes, 16 * 1024 * 1024);
        _ = checked(address + sizeInBytes);
        var addressStr = $"0x{address:X}";
        var xml = FirehoseXmlBuilder.BuildPeek(sizeInBytes, addressStr);
        Log($"Sent <peek> address={addressStr}, size={sizeInBytes}");

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken);
            RequireRawMode(response, EdlErrorCode.PeekFailed);
            using var ms = new MemoryStream();
            await ReadRawPayloadToStreamAsync(ms, sizeInBytes, cancellationToken);
            var completion = await ReceiveTransferCompletionAsync(EdlErrorCode.PeekFailed, cancellationToken);
            return (completion, ms.ToArray());
        }
        catch
        {
            _faulted = true;
            throw;
        }
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
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
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
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
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
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
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
    /// Streams exactly the declared raw bytes. XML-looking payload is still partition data.
    /// </summary>
    /// <param name="destination">The destination stream. 目标流。</param>
    /// <param name="totalBytes">The number of raw bytes to read. 待读取的原始字节数。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    /// <param name="progress">Optional cumulative raw bytes; not evidence of final completion.</param>
    private async Task ReadRawPayloadToStreamAsync(Stream destination, long totalBytes, CancellationToken cancellationToken,
        IProgress<long>? progress = null)
    {
        long bytesRead = 0;
        var rawBuffer = new byte[EdlConstants.RawReadChunkBytes];

        while (bytesRead < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                if (n <= 0 || n > toRead)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout,
                        $"Raw payload read timed out after {bytesRead}/{totalBytes} bytes.");
                }

                chunkBuffer = rawBuffer;
                chunkLength = n;
            }

            await destination.WriteAsync(chunkBuffer.AsMemory(0, chunkLength), cancellationToken);
            bytesRead += chunkLength;

            if (chunkBuffer == _rxBuffer)
            {
                ShiftRxBufferLeft(chunkLength);
            }
            progress?.Report(bytesRead);
        }

    }
}