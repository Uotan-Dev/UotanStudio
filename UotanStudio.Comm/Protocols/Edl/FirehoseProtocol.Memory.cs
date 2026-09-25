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
    /// <param name="startSector">The starting sector.<para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors.<para>扇区数量。</para></param>
    /// <param name="filename">The target filename.<para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number.<para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A tuple containing the response and data.<para>包含响应和数据的元组。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The validated byte span exceeds 16 MiB; use <see cref="ReadToStreamAsync"/> instead.<para>校验后的字节跨度超过 16 MiB；请改用 <see cref="ReadToStreamAsync"/>。</para></exception>
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
    /// <param name="destination">Destination stream for the raw data. The caller owns the stream and must keep it writable and open for the whole call.<para>原始数据写入目标流。调用方持有该流，必须在整个调用期间保持可写且未关闭。</para></param>
    /// <param name="startSector">The starting sector.<para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors.<para>扇区数量。</para></param>
    /// <param name="filename">The target filename.<para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number.<para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The command response (raw payload excluded).<para>命令响应（不含原始数据）。</para></returns>
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
    /// <param name="sizeInBytes">The number of bytes to read; must be greater than 0 and no larger than 16 MiB.<para>待读取字节数；必须大于 0 且不超过 16 MiB。</para></param>
    /// <param name="address">The memory address; must be non-negative and the sum with <paramref name="sizeInBytes"/> must not overflow.<para>内存地址；必须非负，且与 <paramref name="sizeInBytes"/> 之和不得溢出。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A tuple containing the response and the read data.<para>包含响应与读取数据的元组。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="address"/> is negative, <paramref name="sizeInBytes"/> is 0 or negative, or <paramref name="sizeInBytes"/> exceeds 16 MiB.
    /// <para><paramref name="address"/> 为负、<paramref name="sizeInBytes"/> 为 0 或负数，或 <paramref name="sizeInBytes"/> 超过 16 MiB。</para></exception>
    /// <exception cref="OverflowException">
    /// <paramref name="address"/> plus <paramref name="sizeInBytes"/> exceeds <see cref="long.MaxValue"/>.
    /// <para><paramref name="address"/> 与 <paramref name="sizeInBytes"/> 之和超出 <see cref="long.MaxValue"/>。</para></exception>
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
    /// <param name="sizeInBytes">The number of bytes to write; must equal <c>value.Length</c>.<para>待写入字节数；必须等于 <c>value.Length</c>。</para></param>
    /// <param name="address">The memory address.<para>内存地址。</para></param>
    /// <param name="value">The bytes to write, encoded by the caller in target byte order.<para>待写入的字节，由调用方按目标字节序编码。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The Firehose response.<para>Firehose 响应。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.<para><paramref name="value"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><c>value.Length</c> does not match <paramref name="sizeInBytes"/>.<para><c>value.Length</c> 与 <paramref name="sizeInBytes"/> 不一致。</para></exception>
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
    /// <param name="address">The target address.<para>目标地址。</para></param>
    /// <param name="value">The 32-bit value.<para>32 位值。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The Firehose response.<para>Firehose 响应。</para></returns>
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
    /// <param name="dstOffset">The destination address.<para>目标地址。</para></param>
    /// <param name="srcOffset">The source address.<para>源地址。</para></param>
    /// <param name="size">The copy size in bytes.<para>拷贝大小（字节）。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The Firehose response.<para>Firehose 响应。</para></returns>
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