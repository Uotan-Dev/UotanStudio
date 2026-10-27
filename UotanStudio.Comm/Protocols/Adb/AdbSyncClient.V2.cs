using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Compress;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Sync v2 wire paths (stat_v2, ls_v2, sendrecv_v2), following the AOSP file sync protocol: the
/// stat/list responses use the 72/76-byte little-endian v2 records, push streams a single
/// continuous compressed or raw byte stream as DATA blocks of at most 64 KiB terminated by DONE,
/// and pull feeds received DATA blocks into one continuous decompression stream until DONE.
/// <para>sync v2 线上路径（stat_v2、ls_v2、sendrecv_v2），遵循 AOSP file sync 协议：stat/list 应答采用
/// 72/76 字节小端 v2 记录；push 以不超过 64 KiB 的 DATA 块发送单一连续压缩或原始字节流并以 DONE 结束；
/// pull 把收到的 DATA 块喂入单一连续解压流直至 DONE。</para>
/// </summary>
internal sealed partial class AdbSyncClient
{
    /// <summary>Byte width of the packed sync_stat_v2 body. <para>packed sync_stat_v2 主体的字节宽度。</para></summary>
    private const int V2StatBodySize = 72;

    /// <summary>Byte width of the packed sync_dent_v2 header, including the trailing name length.
    /// <para>packed sync_dent_v2 头部的字节宽度，含末尾的名称长度。</para></summary>
    private const int V2DentHeaderSize = 76;

    /// <summary>Byte width of the sync_send_v2 setup message. <para>sync_send_v2 setup 消息的字节宽度。</para></summary>
    private const int V2SendSetupSize = 12;

    /// <summary>Byte width of the sync_recv_v2 setup message. <para>sync_recv_v2 setup 消息的字节宽度。</para></summary>
    private const int V2RecvSetupSize = 8;

    /// <summary>Longest path upstream accepts in a sync request. <para>上游 sync 请求接受的最长路径。</para></summary>
    private const uint MaxSyncRequestPathLength = 1024;

    /// <summary>Bounded count of in-flight compressed blocks between the wire loop and the codec task.
    /// <para>线上循环与编解码任务之间在途压缩块的有界数量。</para></summary>
    private const int CodecChannelCapacity = 16;

    /// <summary>Stats a remote path, using the 72-byte v2 record when the device advertises stat_v2.
    /// <para>查询远端路径；设备通告 stat_v2 时使用 72 字节 v2 记录。</para></summary>
    public async ValueTask<SyncEntry64?> Stat64Async(string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);

        if (!Capabilities.StatV2)
        {
            var legacy = await StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
            return legacy is null ? null : SyncEntry64FromEntry(legacy.Value);
        }

        var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(BuildV1Packet(SyncIds.StatV2, Encoding.UTF8.GetBytes(remotePath)), cancellationToken)
            .ConfigureAwait(false);

        var id = await ReadV2ResponseIdAsync(stream, "STAT", cancellationToken).ConfigureAwait(false);
        if (!id.Span.SequenceEqual("STA2"u8))
        {
            throw new InvalidDataException($"Unexpected sync response id '{Encoding.ASCII.GetString(id.Span)}' to STAT v2.");
        }

        var tail = await ReadExactAsync(stream, V2StatBodySize - IdSize, cancellationToken).ConfigureAwait(false);
        var body = new byte[V2StatBodySize];
        id.Span.CopyTo(body);
        tail.CopyTo(body.AsSpan(IdSize));
        var error = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        if (error != 0)
        {
            CommTrace.State("adb-sync", $"stat v2 '{remotePath}' failed with device errno {error}.");
            return null;
        }

        var name = Path.GetFileName(remotePath.TrimEnd('/'));
        return SyncEntry64FromV2Body(body, name);
    }

    /// <summary>Lists a remote directory, using v2 records when the device advertises ls_v2.
    /// <para>列出远端目录；设备通告 ls_v2 时使用 v2 记录。</para></summary>
    public async ValueTask<IReadOnlyList<SyncEntry64>> List64Async(string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);

        if (!Capabilities.LsV2)
        {
            var legacy = await ListAsync(remotePath, cancellationToken).ConfigureAwait(false);
            return [.. legacy.Select(SyncEntry64FromEntry)];
        }

        var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(BuildV1Packet(SyncIds.ListV2, Encoding.UTF8.GetBytes(remotePath)), cancellationToken)
            .ConfigureAwait(false);

        var entries = new List<SyncEntry64>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var header = await ReadExactAsync(stream, V2DentHeaderSize, cancellationToken).ConfigureAwait(false);
            var id = header.AsSpan(0, IdSize);

            if (id.SequenceEqual("DONE"u8))
            {
                break;
            }

            if (id.SequenceEqual("FAIL"u8))
            {
                var msglen = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(IdSize, 4));
                var err = await ReadExactAsync(stream, CheckedChunkLength(msglen, "LIST fail message"), cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidDataException(
                    $"sync LIST failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
            }

            if (!id.SequenceEqual("DNT2"u8))
            {
                throw new InvalidDataException($"Unexpected sync response id '{Encoding.ASCII.GetString(id)}' to LIST v2.");
            }

            var namelen = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(V2DentHeaderSize - 4, 4));
            if (namelen > MaxDentNameLength)
            {
                throw new InvalidDataException($"DENT v2 name length {namelen} exceeds {MaxDentNameLength} bytes.");
            }

            var nameBytes = await ReadExactAsync(stream, CheckedChunkLength(namelen, "DENT v2 name"), cancellationToken)
                .ConfigureAwait(false);
            if (nameBytes.AsSpan().ContainsAny((byte)'/', (byte)'\\'))
            {
                throw new InvalidDataException("DENT v2 name contains a path separator.");
            }

            var error = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (error != 0)
            {
                CommTrace.State("adb-sync",
                    $"LIST v2 skips entry '{Encoding.UTF8.GetString(nameBytes)}' with device errno {error}.");
                continue;
            }

            entries.Add(SyncEntry64FromV2Body(header, Encoding.UTF8.GetString(nameBytes)));
        }

        return entries;
    }

    /// <summary>
    /// Projects a v1 entry onto the v2 record shape with the extended fields zeroed.
    /// <para>把 v1 条目投影到 v2 记录形态，扩展字段为零。</para>
    /// </summary>
    private static SyncEntry64 SyncEntry64FromEntry(SyncEntry entry) => new(
        entry.Mode, 0, 0, 0, 0, 0, entry.Size, 0, entry.Time, 0, entry.Name);

    /// <summary>
    /// Parses a packed v2 stat/dent body (after the id and error fields) into a record, following
    /// the <c>sync_stat_v2</c> field order: dev, ino, mode, nlink, uid, gid, size, atime, mtime, ctime.
    /// <para>把 packed 的 v2 stat/dent 主体（id 与 error 字段之后）按 <c>sync_stat_v2</c> 字段顺序解析为记录：
    /// dev、ino、mode、nlink、uid、gid、size、atime、mtime、ctime。</para>
    /// </summary>
    private static SyncEntry64 SyncEntry64FromV2Body(ReadOnlySpan<byte> body, string name)
    {
        var dev = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(body[16..]);
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(body[24..]);
        var nlink = BinaryPrimitives.ReadUInt32LittleEndian(body[28..]);
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(body[32..]);
        var gid = BinaryPrimitives.ReadUInt32LittleEndian(body[36..]);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(body[40..]);
        var atime = BinaryPrimitives.ReadInt64LittleEndian(body[48..]);
        var mtime = BinaryPrimitives.ReadInt64LittleEndian(body[56..]);
        var ctime = BinaryPrimitives.ReadInt64LittleEndian(body[64..]);
        return new SyncEntry64(mode, dev, inode, nlink, uid, gid, size, atime, mtime, ctime, name);
    }

    /// <summary>
    /// Reads a 4-byte sync response id, translating FAIL into a detailed exception.
    /// <para>读取 4 字节 sync 应答标识，把 FAIL 转换为带详情的异常。</para>
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> ReadV2ResponseIdAsync(AdbStream stream, string operation,
        CancellationToken cancellationToken = default)
    {
        await ReadExactIntoAsync(stream, _headerScratch, IdSize, cancellationToken).ConfigureAwait(false);
        var id = _headerScratch.AsMemory(0, IdSize);
        if (id.Span.SequenceEqual("FAIL"u8))
        {
            var errLength = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
            var err = await ReadExactAsync(stream, CheckedChunkLength(errLength, $"{operation} fail message"),
                cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException(
                $"sync {operation} failed: {Encoding.UTF8.GetString(err)}");
        }

        return id;
    }

    /// <summary>
    /// Pushes the stream with the sync v2 SND2 framing: path request, mode/flags setup, one
    /// continuous (optionally compressed) DATA-block stream and a DONE terminator.
    /// <para>以 sync v2 SND2 帧推送流：路径请求、mode/flags setup、单一连续（可选压缩）DATA 块流与 DONE 终止。</para>
    /// </summary>
    private async ValueTask PushStreamV2Async(Stream source, string remotePath, uint mode, uint mtime,
        AdbSyncCompression codec, bool dryRun, CancellationToken cancellationToken)
    {
        var pathBytes = Encoding.UTF8.GetBytes(remotePath);
        if (pathBytes.Length > MaxSyncRequestPathLength)
        {
            throw new InvalidDataException(
                $"Sync push path length {pathBytes.Length} exceeds the {MaxSyncRequestPathLength}-byte limit.");
        }

        var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false);
        var flags = Capabilities.ResolveFlags(codec, dryRun);
        var stopwatch = Stopwatch.StartNew();
        long totalBytes = 0;
        var chunks = 0;
        CommTrace.State("adb-sync",
            $"push v2 start remote='{remotePath}' codec={codec} dryRun={dryRun} mode=0{Convert.ToString(mode, 8)} mtime={mtime}.");

        if (codec == AdbSyncCompression.None)
        {
            try
            {
                await SendV2SetupAsync(stream, SyncIds.SendV2, pathBytes, V2SendSetupSize,
                    writer => WriteSendSetup(writer, mode, flags), cancellationToken).ConfigureAwait(false);
                var packet = _packet ??= new byte[V1HeaderSize + _outChunk];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await source.ReadAsync(packet.AsMemory(V1HeaderSize, _outChunk), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    WritePacketHeader(packet, SyncIds.Data, read);
                    await stream.WriteAsync(packet.AsMemory(0, V1HeaderSize + read), cancellationToken).ConfigureAwait(false);
                    totalBytes += read;
                    chunks++;
                }

                await SendDoneAsync(stream, mtime, cancellationToken).ConfigureAwait(false);
                await ExpectResponseAsync(stream, "push", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CommTrace.Error("adb-sync",
                    $"push v2 failed remote='{remotePath}' after {totalBytes} B / {chunks} chunk(s): {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            CommTrace.State("adb-sync",
                $"push v2 done remote='{remotePath}': {totalBytes} B in {chunks} chunk(s), {stopwatch.ElapsedMilliseconds} ms.");
            return;
        }

        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(CodecChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        using var encodeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var encodeTask = Task.Run(() => EncodeSourceAsync(source, channel, codec, _outChunk, encodeCts.Token), encodeCts.Token);
        try
        {
            await SendV2SetupAsync(stream, SyncIds.SendV2, pathBytes, V2SendSetupSize,
                writer => WriteSendSetup(writer, mode, flags), cancellationToken).ConfigureAwait(false);

            var packet = _packet ??= new byte[V1HeaderSize + _outChunk];
            await foreach (var block in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WritePacketHeader(packet, SyncIds.Data, block.Length);
                block.CopyTo(packet.AsMemory(V1HeaderSize));
                await stream.WriteAsync(packet.AsMemory(0, V1HeaderSize + block.Length), cancellationToken)
                    .ConfigureAwait(false);
                totalBytes += block.Length;
                chunks++;
                CommTrace.Payload("adb-sync", "push v2 chunk", block.AsSpan());
            }

            await SendDoneAsync(stream, mtime, cancellationToken).ConfigureAwait(false);
            await ExpectResponseAsync(stream, "push", cancellationToken).ConfigureAwait(false);
            await encodeTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            encodeCts.Cancel();
            channel.Writer.TryComplete();
            try
            {
                await encodeTask.ConfigureAwait(false);
            }
            catch
            {
            }

            CommTrace.Error("adb-sync",
                $"push v2 failed remote='{remotePath}' after {totalBytes} B / {chunks} chunk(s): {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        CommTrace.State("adb-sync",
            $"push v2 done remote='{remotePath}': {totalBytes} B compressed in {chunks} chunk(s), {stopwatch.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// Pulls the remote file with the sync v2 RCV2 framing: path request, flags setup, then a
    /// continuous (optionally compressed) DATA-block stream terminated by DONE.
    /// <para>以 sync v2 RCV2 帧拉取远端文件：路径请求、flags setup，随后是单一连续（可选压缩）DATA 块流并以 DONE 结束。</para>
    /// </summary>
    private async ValueTask PullStreamV2Async(string remotePath, Stream destination, AdbSyncCompression codec,
        CancellationToken cancellationToken)
    {
        var pathBytes = Encoding.UTF8.GetBytes(remotePath);
        if (pathBytes.Length > MaxSyncRequestPathLength)
        {
            throw new InvalidDataException(
                $"Sync pull path length {pathBytes.Length} exceeds the {MaxSyncRequestPathLength}-byte limit.");
        }

        var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false);
        var flags = Capabilities.ResolveFlags(codec, dryRun: false);
        var stopwatch = Stopwatch.StartNew();
        long totalBytes = 0;
        var chunks = 0;
        CommTrace.State("adb-sync", $"pull v2 start remote='{remotePath}' codec={codec}.");

        if (codec == AdbSyncCompression.None)
        {
            try
            {
                await SendV2SetupAsync(stream, SyncIds.RecvV2, pathBytes, V2RecvSetupSize,
                    writer => WriteRecvSetup(writer, flags), cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ReadExactIntoAsync(stream, _headerScratch, V1HeaderSize, cancellationToken).ConfigureAwait(false);
                    var id = _headerScratch.AsSpan(0, IdSize);
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(_headerScratch.AsSpan(IdSize));

                    if (id.SequenceEqual("DONE"u8))
                    {
                        break;
                    }

                    if (id.SequenceEqual("FAIL"u8))
                    {
                        var err = await ReadExactAsync(stream, CheckedChunkLength(length, "RECV fail message"),
                            cancellationToken).ConfigureAwait(false);
                        throw new InvalidDataException(
                            $"sync pull failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
                    }

                    if (!id.SequenceEqual("DATA"u8))
                    {
                        throw new InvalidDataException(
                            $"Unexpected sync response id '{Encoding.ASCII.GetString(id)}' to RECV v2.");
                    }

                    await CopyExactAsync(destination, stream, CheckedChunkLength(length, "RECV data"), cancellationToken)
                        .ConfigureAwait(false);
                    totalBytes += length;
                    chunks++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CommTrace.Error("adb-sync",
                    $"pull v2 failed remote='{remotePath}' after {totalBytes} B / {chunks} chunk(s): {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            CommTrace.State("adb-sync",
                $"pull v2 done remote='{remotePath}': {totalBytes} B in {chunks} chunk(s), {stopwatch.ElapsedMilliseconds} ms.");
            return;
        }

        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(CodecChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        using var decodeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? decodeFailure = null;
        var decodeTask = Task.Run(async () =>
        {
            try
            {
                var feed = new SyncBlockFeeder(channel);
                await using var decoder = CreateSyncDecodeStream(feed, codec);
                await decoder.CopyToAsync(destination, (int)InboundMaxChunkBytes, decodeCts.Token);
            }
            catch (Exception ex)
            {
                decodeFailure = ex;
            }
        }, decodeCts.Token);
        try
        {
            await SendV2SetupAsync(stream, SyncIds.RecvV2, pathBytes, V2RecvSetupSize,
                writer => WriteRecvSetup(writer, flags), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadExactIntoAsync(stream, _headerScratch, V1HeaderSize, cancellationToken).ConfigureAwait(false);
                var id = _headerScratch.AsSpan(0, IdSize);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(_headerScratch.AsSpan(IdSize));

                if (id.SequenceEqual("DONE"u8))
                {
                    break;
                }

                if (id.SequenceEqual("FAIL"u8))
                {
                    var err = await ReadExactAsync(stream, CheckedChunkLength(length, "RECV fail message"),
                        cancellationToken).ConfigureAwait(false);
                    throw new InvalidDataException(
                        $"sync pull failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
                }

                if (!id.SequenceEqual("DATA"u8))
                {
                    throw new InvalidDataException(
                        $"Unexpected sync response id '{Encoding.ASCII.GetString(id)}' to RECV v2.");
                }

                var payload = await ReadExactAsync(stream, CheckedChunkLength(length, "RECV data"), cancellationToken)
                    .ConfigureAwait(false);
                if (payload.Length > 0)
                {
                    await channel.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    totalBytes += payload.Length;
                    chunks++;
                }
            }

            channel.Writer.TryComplete();
            await decodeTask.ConfigureAwait(false);
            if (decodeFailure is not null)
            {
                ExceptionDispatchInfo.Capture(decodeFailure).Throw();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            decodeCts.Cancel();
            channel.Writer.TryComplete();
            try
            {
                await decodeTask.ConfigureAwait(false);
            }
            catch
            {
            }

            CommTrace.Error("adb-sync",
                $"pull v2 failed remote='{remotePath}' after {totalBytes} B / {chunks} chunk(s): {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        CommTrace.State("adb-sync",
            $"pull v2 done remote='{remotePath}': {totalBytes} B compressed in {chunks} chunk(s), {stopwatch.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// Sends a sync v2 request: the path request header, the path, then the setup message built by
    /// <paramref name="writeSetup"/>.
    /// <para>发送 sync v2 请求：路径请求头、路径，然后由 <paramref name="writeSetup"/> 构建的 setup 消息。</para>
    /// </summary>
    private async ValueTask SendV2SetupAsync(AdbStream stream, string id, byte[] pathBytes, int setupSize,
        Action<Span<byte>> writeSetup, CancellationToken cancellationToken)
    {
        var packet = new byte[V1HeaderSize + pathBytes.Length + setupSize];
        WritePacketHeader(packet, id, pathBytes.Length);
        pathBytes.CopyTo(packet.AsSpan(V1HeaderSize));
        writeSetup(packet.AsSpan(V1HeaderSize + pathBytes.Length));
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the sync_send_v2 setup message: the id, the mode and the flag word.
    /// <para>写入 sync_send_v2 setup 消息：id、mode 与旗标字。</para>
    /// </summary>
    private static void WriteSendSetup(Span<byte> destination, uint mode, uint flags)
    {
        Encoding.ASCII.GetBytes(SyncIds.SendV2, destination[..IdSize]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], mode);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], flags);
    }

    /// <summary>
    /// Writes the sync_recv_v2 setup message: the id and the flag word.
    /// <para>写入 sync_recv_v2 setup 消息：id 与旗标字。</para>
    /// </summary>
    private static void WriteRecvSetup(Span<byte> destination, uint flags)
    {
        Encoding.ASCII.GetBytes(SyncIds.RecvV2, destination[..IdSize]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], flags);
    }

    /// <summary>
    /// Sends the DONE terminator carrying the modification timestamp.
    /// <para>发送携带修改时间戳的 DONE 终止帧。</para>
    /// </summary>
    private async ValueTask SendDoneAsync(AdbStream stream, uint mtime, CancellationToken cancellationToken)
    {
        stream.ResetResponseSignal();
        var donePacket = new byte[V1HeaderSize];
        Encoding.ASCII.GetBytes(SyncIds.Done, donePacket.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(donePacket.AsSpan(4), mtime);
        await stream.WriteAsync(donePacket, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the source stream through the codec into bounded DATA blocks, completing the channel
    /// on success and faulting it with the encoder failure otherwise.
    /// <para>经编解码器把源流读入有界 DATA 块；成功则完成通道，失败则以编码器异常终结通道。</para>
    /// </summary>
    private static async Task EncodeSourceAsync(Stream source, Channel<byte[]> channel, AdbSyncCompression codec,
        int maxBlock, CancellationToken cancellationToken)
    {
        var collector = new SyncBlockCollector(channel, maxBlock);
        try
        {
            await using (var encoder = CreateSyncEncodeStream(collector, codec))
            {
                await source.CopyToAsync(encoder, maxBlock, cancellationToken).ConfigureAwait(false);
            }

            collector.PushRemainder();
            collector.Complete();
        }
        catch (Exception ex)
        {
            collector.Fail(ex);
        }
    }

    /// <summary>
    /// Creates the codec's compression stream over a block collector; the caller closes the codec
    /// first and then flushes the collector remainder.
    /// <para>在块收集器之上创建编解码器压缩流；调用方先关闭编解码器，再冲刷收集器余量。</para>
    /// </summary>
    private static Stream CreateSyncEncodeStream(Stream collector, AdbSyncCompression codec) => codec switch
    {
        AdbSyncCompression.Brotli => CompressionService.CreateCompressionStream(
            collector, CompressionFormat.Brotli, new CompressionOptions { Level = 1 }),
        AdbSyncCompression.Lz4 => CompressionService.CreateCompressionStream(
            collector, CompressionFormat.Lz4, new CompressionOptions { Level = 0 }),
        AdbSyncCompression.Zstd => CompressionService.CreateCompressionStream(
            collector, CompressionFormat.Zstd, new CompressionOptions { Level = 3 }),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported sync v2 codec."),
    };

    /// <summary>
    /// Creates the codec's decompression stream over a block feeder; closing the codec leaves the
    /// feeder open so the pull loop owns it.
    /// <para>在块供给器之上创建编解码器解压流；关闭编解码器不关闭供给器，供给器由 pull 循环持有。</para>
    /// </summary>
    private static Stream CreateSyncDecodeStream(Stream feed, AdbSyncCompression codec) => codec switch
    {
        AdbSyncCompression.Brotli => CompressionService.CreateDecompressionStream(feed, CompressionFormat.Brotli),
        AdbSyncCompression.Lz4 => CompressionService.CreateDecompressionStream(feed, CompressionFormat.Lz4),
        AdbSyncCompression.Zstd => CompressionService.CreateDecompressionStream(feed, CompressionFormat.Zstd),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported sync v2 codec."),
    };

    /// <summary>
    /// Collects compressed output into DATA-block-sized buffers pushed to the wire channel.
    /// <para>把压缩输出收集为 DATA 块大小的缓冲并推入线上通道。</para>
    /// </summary>
    private sealed class SyncBlockCollector(Channel<byte[]> channel, int maxBlock) : Stream
    {
        private readonly byte[] _block = new byte[maxBlock];
        private int _filled;
        private bool _completed;

        /// <summary>Flushes any buffered partial block to the channel. <para>把缓冲中的不完整块冲刷到通道。</para></summary>
        public void PushRemainder()
        {
            if (_filled > 0)
            {
                var remainder = new byte[_filled];
                _block.AsSpan(0, _filled).CopyTo(remainder);
                Publish(remainder);
                _filled = 0;
            }
        }

        /// <summary>Completes the channel after a successful encode. <para>编码成功后完成通道。</para></summary>
        public void Complete()
        {
            if (!_completed)
            {
                _completed = true;
                channel.Writer.TryComplete();
            }
        }

        /// <summary>Faults the channel after an encode failure. <para>编码失败后以异常终结通道。</para></summary>
        public void Fail(Exception failure)
        {
            if (!_completed)
            {
                _completed = true;
                channel.Writer.TryComplete(failure);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var take = Math.Min(count, _block.Length - _filled);
                Buffer.BlockCopy(buffer, offset, _block, _filled, take);
                _filled += take;
                offset += take;
                count -= take;
                if (_filled == _block.Length)
                {
                    var full = new byte[_block.Length];
                    _block.CopyTo(full, 0);
                    Publish(full);
                    _filled = 0;
                }
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        private void Publish(byte[] block)
        {
            if (!channel.Writer.TryWrite(block))
            {
                throw new IOException("Sync encoder channel rejected a block.");
            }
        }

        public override void Flush()
        {
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Feeds received compressed blocks to a codec stream; reads block until the channel completes,
    /// then report end-of-stream.
    /// <para>把收到的压缩块供给编解码器流；读取会阻塞直至通道完成，然后报告流结束。</para>
    /// </summary>
    private sealed class SyncBlockFeeder(Channel<byte[]> channel) : Stream
    {
        private byte[]? _current;
        private int _offset;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                if (_current is null)
                {
                    if (!channel.Reader.TryRead(out _current))
                    {
                        var moved = channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult();
                        if (!moved || !channel.Reader.TryRead(out _current))
                        {
                            return total;
                        }
                    }

                    _offset = 0;
                }

                var take = Math.Min(count - total, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset + total, take);
                _offset += take;
                total += take;
                if (_offset >= _current.Length)
                {
                    _current = null;
                }
            }

            return total;
        }

        public override void Flush()
        {
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}