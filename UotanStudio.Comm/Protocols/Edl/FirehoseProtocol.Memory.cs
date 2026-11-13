using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Reports how much of a cancelled raw transfer the host has taken back from the target while returning it to the
/// Firehose command boundary. The bytes in this report are discarded rather than saved, so it must never be used as
/// evidence that a read or write progressed.
/// <para>上报主机在把被取消的原始传输送回 Firehose 命令边界的过程中，已从目标收回多少字节。这里上报的字节是被丢弃
/// 而非保存的，因此绝不能作为读取或写入有进展的证据。</para>
/// </summary>
/// <param name="DrainedBytes">Bytes already taken back from the target.<para>已从目标收回的字节数。</para></param>
/// <param name="TotalBytes">Whole payload the target declared.<para>目标声明的完整负载。</para></param>
public readonly record struct EdlQuiesceProgress(long DrainedBytes, long TotalBytes);

/// <summary>
/// Firehose protocol partition read and memory peek operations.
/// <para>Firehose 协议的分区读取与内存 peek 读取操作。</para>
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
        string filename, int physicalPartition, CancellationToken cancellationToken, IProgress<long>? progress = null,
        IProgress<EdlQuiesceProgress>? stoppingProgress = null)
    {
        ThrowIfDisposed();
        RequireCapability(EdlFirehoseCommand.Read);
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
        var transferStarted = false;
        try
        {
            var xml = FirehoseXmlBuilder.BuildRead(Configuration.SectorSize, numSectors, filename, physicalPartition,
                startSector, slot: EffectiveSlot, pagesPerBlock: NandPagesPerBlockHint);
            var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken,
                skipResponseAcknowledgement: Configuration.SkipResponseOnReadWrite ? SkipResponseAcknowledgement(true) : null);
            RequireRawMode(response, EdlErrorCode.ReadFailed);
            transferStarted = true;
            var outcome = await ReadRawPayloadToStreamAsync(destination, totalBytes, cancellationToken, progress,
                stoppingProgress);
            if (outcome is not RawPayloadOutcome.Completed)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return await ReceiveTransferCompletionAsync(EdlErrorCode.ReadFailed, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            if (_faulted || !transferStarted)
            {
                InvalidateTransfer("the read stopped before its raw payload reached the command boundary");
            }

            RecordFailure(ex, transferStarted, payloadSent: false);
            throw;
        }
        catch (Exception ex)
        {
            _faulted = true;
            RecordFailure(ex, transferStarted, payloadSent: false);
            throw;
        }
    }

    /// <summary>Streams a partition range into the destination while hashing every written byte, detecting short reads, and reporting cumulative progress. Large reads never allocate a single destination-sized buffer; the transport works chunk by chunk.
    /// <para>将分区范围流式写入目标，同时对每个写入字节计算哈希、检测短读并上报累计进度。 大读取绝不会分配等同于目标大小的单块缓冲；传输按分块推进。</para></summary>
    /// <remarks>The caller owns <paramref name="destination"/>. Atomic publication (temp file then rename) is a host responsibility: a cancelled or failed read leaves the destination partially written and it must never be published as a completed backup.
    /// <para>调用方持有 <paramref name="destination"/>。原子发布（临时文件后重命名）是宿主职责： 被取消或失败的读取会留下部分写入的目标，绝不能作为完成备份发布。</para></remarks>
    /// <param name="destination">Writable destination stream.<para>可写目标流。</para></param>
    /// <param name="startSector">The starting sector.<para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors.<para>扇区数量。</para></param>
    /// <param name="filename">The target filename.<para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number.<para>物理分区编号。</para></param>
    /// <param name="progress">Optional cumulative transferred bytes; not evidence of final completion.
    /// <para>可选的累计传输字节；不是最终完成的证据。</para></param>
    /// <param name="expectedSha256Hex">Optional lowercase hex SHA-256 the transferred bytes must match.
    /// <para>可选的十六进制小写 SHA-256；传输字节必须匹配。</para></param>
    /// <param name="stoppingProgress">Optional reporter for the discarded bytes a cancelled read takes back before it
    /// returns the target to command mode; never evidence of saved data.
    /// <para>可选报告器；取消的读取在把目标送回命令模式前收回并丢弃的字节，不能作为数据已保存的证据。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The measured read result with device acknowledgement state.
    /// <para>带设备确认状态的计量读取结果。</para></returns>
    /// <exception cref="EdlException">Thrown when the device short-reads, fails the completion acknowledgement, or the measured hash contradicts <paramref name="expectedSha256Hex"/>.
    /// <para>设备短读、完成确认失败，或实测哈希与 <paramref name="expectedSha256Hex"/> 矛盾时抛出。</para></exception>
    public async Task<EdlStreamReadResult> ReadPartitionToStreamAsync(Stream destination, long startSector, long numSectors,
        string filename = "partition.bin", int physicalPartition = 0, IProgress<long>? progress = null,
        string? expectedSha256Hex = null, IProgress<EdlQuiesceProgress>? stoppingProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(destination);
        var totalBytes = ValidateStorageRange(startSector, numSectors, physicalPartition);
        await using var hashing = new DigestWriteStream(destination);
        var response = await ReadToStreamCoreAsync(hashing, startSector, numSectors, filename, physicalPartition,
            cancellationToken, progress, stoppingProgress).ConfigureAwait(false);
        await hashing.FlushAsync(cancellationToken).ConfigureAwait(false);
        var written = hashing.WrittenBytes;
        if (written != totalBytes)
        {
            throw new EdlException(EdlErrorCode.ReadFailed,
                $"Short read detected: the device stream produced {written} of {totalBytes} declared bytes.")
            {
                FailureKind = EdlFailureKind.ExecutionUnknown
            };
        }

        var sha256Hex = hashing.GetSha256Hex();
        if (expectedSha256Hex is { Length: > 0 } expected
            && !string.Equals(sha256Hex, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new EdlException(EdlErrorCode.OnTargetVerificationFailed,
                $"The read-back SHA-256 {sha256Hex} contradicts the expected {expected}.")
            {
                FailureKind = EdlFailureKind.ResourceMismatch
            };
        }

        return new EdlStreamReadResult(written, totalBytes, sha256Hex, response);
    }

    /// <summary>
    /// A write-through stream that hashes every byte appended and never disposes the wrapped
    /// destination.
    /// <para>透传写入流，对每个追加字节计算哈希，且绝不释放被包装的目标流。</para>
    /// </summary>
    private sealed class DigestWriteStream(Stream inner) : Stream
    {
        private readonly DigestAccumulator _hash = Sha256.CreateAccumulator();

        internal long WrittenBytes { get; private set; }

        internal string GetSha256Hex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            var span = buffer.AsSpan(offset, count);
            _hash.Append(span);
            inner.Write(span);
            WrittenBytes += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.Append(buffer);
            inner.Write(buffer);
            WrittenBytes += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.Append(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            WrittenBytes += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    /// <summary>Reads memory from the target device via &lt;peek&gt;.
    /// <para>通过 &lt;peek&gt; 从目标设备读取内存。</para></summary>
    /// <param name="sizeInBytes">The number of bytes to read; must be greater than 0 and no larger than 16 MiB.
    /// <para>待读取字节数；必须大于 0 且不超过 16 MiB。</para></param>
    /// <param name="address">The memory address; must be non-negative and the sum with <paramref name="sizeInBytes"/> must not overflow.
    /// <para>内存地址；必须非负，且与 <paramref name="sizeInBytes"/> 之和不得溢出。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A tuple containing the response and the read data.<para>包含响应与读取数据的元组。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="address"/> is negative, <paramref name="sizeInBytes"/> is 0 or negative, or <paramref name="sizeInBytes"/> exceeds 16 MiB.
    /// <para><paramref name="address"/> 为负、<paramref name="sizeInBytes"/> 为 0 或负数，或 <paramref name="sizeInBytes"/> 超过 16 MiB。</para></exception>
    /// <exception cref="OverflowException"><paramref name="address"/> plus <paramref name="sizeInBytes"/> exceeds <see cref="long.MaxValue"/>.
    /// <para><paramref name="address"/> 与 <paramref name="sizeInBytes"/> 之和超出 <see cref="long.MaxValue"/>。</para></exception>
    public async Task<(FirehoseResponse Response, byte[] Data)> PeekAsync(long sizeInBytes, long address, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await PeekCoreAsync(sizeInBytes, address, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the peek exchange on an already-owned operation lease; the caller must hold the lease.
    /// A NAK triggers one retry with the uppercase <c>SizeInBytes</c> attribute spelling some
    /// programmers require, mirroring the poke path.
    /// <para>在已持有的操作租约上执行 peek 交换；调用方必须已持有该租约。收到 NAK 时按部分
    /// programmer 要求的大写 <c>SizeInBytes</c> 属性拼写重试一次，与 poke 路径一致。</para>
    /// </summary>
    internal async Task<(FirehoseResponse Response, byte[] Data)> PeekCoreAsync(long sizeInBytes, long address,
        CancellationToken cancellationToken)
    {
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
            if (!response.IsAck)
            {
                Log("The target rejected <peek>; retrying with the SizeInBytes attribute spelling.");
                response = await SendCommandAndWaitResponseAsync(
                    FirehoseXmlBuilder.BuildPeek(sizeInBytes, addressStr, uppercaseSize: true), cancellationToken);
            }

            RequireRawMode(response, EdlErrorCode.PeekFailed);
            using var ms = new MemoryStream();
            var outcome = await ReadRawPayloadToStreamAsync(ms, sizeInBytes, cancellationToken);
            if (outcome is not RawPayloadOutcome.Completed)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var completion = await ReceiveTransferCompletionAsync(EdlErrorCode.PeekFailed, cancellationToken);
            return (completion, ms.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_faulted)
            {
                InvalidateTransfer("the peek stopped before its raw payload reached the command boundary");
            }

            throw;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>
    /// Streams exactly the declared raw bytes. XML-looking payload is still partition data.
    /// <para>精确流出声明的原始字节。看起来像 XML 的负载仍然是分区数据。</para>
    /// Read requests never exceed the payload size the target negotiated for data travelling from
    /// the target, so one transport read stays inside one target packet.
    /// <para>读取请求不会超过目标设备为“自目标方向”协商的负载大小，因此单次传输读取保持在目标的
    /// 一个数据包之内。</para>
    /// A zero-length completion delimits one negotiated payload block rather than reporting an expired deadline;
    /// silence is bounded both in read count and in elapsed time since the last byte that arrived, so a stalled
    /// target still fails while a slow one is read to the end. There is no budget over the transfer as a whole.
    /// <para>零长度完成标记的是一个协商负载块的边界，而不是截止时间已过；静默同时按读取次数和“距最后一次真正
    /// 收到字节”的时长设界，因此中途停住的目标仍会失败，而慢速目标会被读完。整次传输没有总时长预算。</para>
    /// </summary>
    /// <param name="destination">The destination stream. <para>目标流。</para></param>
    /// <param name="totalBytes">The number of raw bytes to read. <para>待读取的原始字节数。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The outcome of the payload phase; a cancellation is quiesced before this returns.
    /// <para>负载阶段的结果；返回前已完成取消的收尾。</para></returns>
    /// <param name="progress">Optional cumulative raw bytes; not evidence of final completion.<para>可选的累计原始字节数；不能作为最终完成的证据。</para></param>
    /// <param name="stoppingProgress">Optional reporter for the discarded bytes a cancellation takes back before this
    /// returns; never evidence that the read progressed.
    /// <para>可选报告器；取消后本次返回前收回并丢弃的字节，不能作为读取有进展的证据。</para></param>
    private async Task<RawPayloadOutcome> ReadRawPayloadToStreamAsync(Stream destination, long totalBytes,
        CancellationToken cancellationToken, IProgress<long>? progress = null,
        IProgress<EdlQuiesceProgress>? stoppingProgress = null)
    {
        long bytesRead = 0;
        var chunkBytes = GetRawReadChunkSize();
        var rawBuffer = new byte[chunkBytes];
        var silentReads = 0;
        // Nothing on the clock bounds this phase. A target that keeps delivering its declared payload is read to the
        // end however slow the link is, because the bytes it still owes are the only way back to command mode; an
        // absolute budget bounded the transfer instead of the stall, so a healthy multi-gigabyte read on a slow link
        // died partway through and left the target in raw mode. Only silence ends the read, measured from the last
        // byte that actually arrived.
        // 本阶段没有任何时钟上界。只要目标还在发送声明的负载就一路读完，无论链路多慢，因为它欠的字节是回到命令模式
        // 的唯一途径；按绝对时长设界界住的是整次传输而不是停摆，于是一次健康的多 GB 读取会在慢链路上半途死掉并把目标
        // 留在原始模式。只有静默能结束读取，且静默从真正收到最后一个字节的时刻起算。
        var lastDeliveryAt = Environment.TickCount64;

        try
        {
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
                    silentReads = 0;
                    lastDeliveryAt = Environment.TickCount64;
                }
                else
                {
                    var toRead = (int)Math.Min(chunkBytes, totalBytes - bytesRead);
                    int n;
                    try
                    {
                        n = await _transport.ReadAsync(rawBuffer, 0, toRead, _cmdTimeoutMs, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // One read that returns nothing is a slow block, not a dead link: block boundaries and
                        // back-pressure both look like this. It only becomes a stall once the link has delivered
                        // nothing for the whole silence window below, so the timeout is counted there instead of
                        // ending a transfer that is still making progress.
                        // 一次读不到东西是慢块而不是死链路：块边界与反压都是这个样子。只有当链路在整个静默窗口内都没有
                        // 供数时才算停摆，因此这次超时在下面的窗口里计数，而不是直接结束仍在推进的传输。
                        n = 0;
                    }

                    if (n < 0 || n > toRead)
                    {
                        throw FailStalledRawRead(
                            $"The transport reported {n} byte(s) for a {toRead}-byte read at " +
                            $"{bytesRead}/{totalBytes} bytes.");
                    }

                    if (n == 0)
                    {
                        // A zero-length completion is not an expired deadline: the programmer ends each negotiated
                        // payload block with one, so the transport answers the next request with "nothing yet" at
                        // every block boundary. Silence still has to end, so it is bounded both in count and in
                        // elapsed time; either bound ends the read instead of spinning it.
                        // 零长度完成不是截止时间已过：programmer 在每个协商负载块边界发出一次，因此每个块边界上传输都会
                        // 回答“暂时没有”。静默仍必须结束，所以它同时有次数与时长两个上限；任一触发都结束读取，而不是空转。
                        if (++silentReads > MaximumSilentRawReads)
                        {
                            throw FailStalledRawRead(
                                $"Raw payload read went silent after {bytesRead}/{totalBytes} bytes.");
                        }

                        if (Environment.TickCount64 - lastDeliveryAt > RawSilenceTimeoutMs)
                        {
                            throw FailStalledRawRead(
                                $"The target delivered nothing for {RawSilenceTimeoutMs} ms with " +
                                $"{bytesRead}/{totalBytes} bytes read.");
                        }

                        await Task.Delay(ZeroLengthReadRetryDelayMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    silentReads = 0;
                    lastDeliveryAt = Environment.TickCount64;
                    chunkBuffer = rawBuffer;
                    chunkLength = n;
                }

                // One iteration is accounted for as a whole: writing the destination must not be interrupted by the
                // already-cancelled token after the bytes left the endpoint, or the quiesce would over-read.
                // 一次迭代整体记账：字节离开端点后，写目标流不得再被已取消的令牌打断，否则收尾会多读。
                await destination.WriteAsync(chunkBuffer.AsMemory(0, chunkLength), CancellationToken.None);
                bytesRead += chunkLength;

                if (chunkBuffer == _rxBuffer)
                {
                    ShiftRxBufferLeft(chunkLength);
                }
                progress?.Report(bytesRead);
            }

            return RawPayloadOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await QuiesceCancelledRawReadAsync(totalBytes - bytesRead, chunkBytes, totalBytes,
                stoppingProgress).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Delay between two reads that completed with a zero length, so a target that keeps answering an empty
    /// completion immediately cannot spin the budget away.
    /// <para>两次零长度完成读取之间的停顿，使立即返回空完成的目标无法以紧循环耗掉预算。</para>
    /// </summary>
    private const int ZeroLengthReadRetryDelayMs = 2;

    /// <summary>
    /// How many consecutive zero-length completions are tolerated before the payload is reported as silent.
    /// The programmer emits one at every negotiated payload block boundary, so the bound only has to cover the
    /// programmable block density of a slow target and never the whole transfer.
    /// <para>连续多少次零长度完成之后把负载报告为静默。programmer 在每个协商负载块边界发出一次，
    /// 因此该上限只需覆盖慢速目标的块间距，而不是整次传输。</para>
    /// </summary>
    private const int MaximumSilentRawReads = 512;

    /// <summary>
    /// Rejects a raw payload phase that stopped making progress and marks the link unusable. A target that stops
    /// delivering cannot be drained back to the command boundary because there is nothing left to read, so the only
    /// exit that leaves no stale payload behind is an endpoint reset plus an explicit reconnect requirement. Handing
    /// the session back as reusable would let the next command parse whatever the target was still streaming.
    /// <para>拒绝已停止推进的原始负载阶段并标记链路不可用。停止供数的目标无法被排空回命令边界，因为已经没有东西
    /// 可读，因此唯一不留下过期负载的出口就是复位端点并明确要求重连。若把会话当成可复用交回去，下一条命令解析到的
    /// 就是目标还在发送的东西。</para>
    /// </summary>
    /// <param name="message">What the caller should see, including the byte position.
    /// <para>调用方应看到的信息，含字节位置。</para></param>
    /// <returns>The exception the caller should throw.<para>调用方应抛出的异常。</para></returns>
    private EdlException FailStalledRawRead(string message)
    {
        InvalidateTransfer(message);
        return new EdlException(EdlErrorCode.ReadPortTimeout, message);
    }

    /// <summary>
    /// Resolves the raw payload read chunk: the <see cref="EdlConstants.RawReadChunkBytes"/> ceiling
    /// narrowed to the payload size the target negotiated for data sent from itself. A non-positive
    /// negotiation falls back to the ceiling rather than stalling one byte at a time.
    /// <para>解析原始负载的读取块大小：以 <see cref="EdlConstants.RawReadChunkBytes"/> 为上界，
    /// 并按目标设备为其自身发出数据所协商的负载大小收窄。协商值非正时回退到上界，而不是逐字节停滞。</para>
    /// </summary>
    /// <returns>The chunk size in bytes, between 1 and <see cref="EdlConstants.RawReadChunkBytes"/>.
    /// <para>读取块字节数，介于 1 与 <see cref="EdlConstants.RawReadChunkBytes"/> 之间。</para></returns>
    private int GetRawReadChunkSize()
    {
        var negotiated = Configuration.MaxPayloadSizeFromTarget;
        return negotiated <= 0
            ? EdlConstants.RawReadChunkBytes
            : checked((int)Math.Clamp(negotiated, 1L, (long)EdlConstants.RawReadChunkBytes));
    }
}