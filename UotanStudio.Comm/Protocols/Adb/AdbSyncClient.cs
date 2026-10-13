using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Directory entry from sync STAT / LIST.
/// <para>sync STAT / LIST 返回的目录条目。</para>
/// </summary>
/// <param name="mode">The file mode bits (st_mode). <para>文件模式位。</para></param>
/// <param name="size">The file size in bytes. <para>文件大小（字节）。</para></param>
/// <param name="time">The modification time as Unix seconds. <para>修改时间（Unix 秒）。</para></param>
/// <param name="name">The entry name. <para>条目名称。</para></param>
public readonly struct SyncEntry(uint mode, uint size, uint time, string name)
{
    /// <summary>Entry mode bits (st_mode) as reported by sync STAT / LIST.
    /// <para>sync STAT / LIST 报告的条目模式位（st_mode）。</para></summary>
    /// <value>The little-endian DWORD decoded from the wire body; encodes file type and permission bits.
    /// <para>从线上数据体解码的小端 DWORD；编码了文件类型与权限位。</para></value>
    public uint Mode { get; } = mode;

    /// <summary>Entry size in bytes. <para>条目大小（字节）。</para></summary>
    /// <value>The little-endian DWORD from the wire body; meaningful for regular files.
    /// <para>线上数据体中的小端 DWORD；对普通文件有意义。</para></value>
    public uint Size { get; } = size;

    /// <summary>Modification time as Unix seconds. <para>修改时间（Unix 秒）。</para></summary>
    /// <value>The little-endian DWORD from the wire body.
    /// <para>线上数据体中的小端 DWORD。</para></value>
    public uint Time { get; } = time;

    /// <summary>Entry name within its directory. <para>条目在其目录内的名称。</para></summary>
    /// <value>The UTF-8 name from a LIST record, or the file-name portion of the queried path for STAT.
    /// <para>来自 LIST 记录的 UTF-8 名称；STAT 时则为被查询路径的文件名部分。</para></value>
    public string Name { get; } = name;
}

/// <summary>
/// Client for the ADB sync service ("sync:"), implementing file transfer
/// (SEND / RECV / STAT / LIST) over the v1 wire format, which adbd accepts even when sendrecv_v2 is negotiated.
/// <para>ADB sync 服务客户端，以 v1 线上格式实现文件传输（SEND / RECV / STAT / LIST）。</para>
/// </summary>
/// <param name="connection">The ADB connection.<para>ADB 连接。</para></param>
/// <param name="maxChunk">Requested outbound chunk size in file bytes.<para>请求的出站数据块大小（文件字节数）。</para></param>
internal sealed class AdbSyncClient(AdbConnection connection, uint maxChunk = AdbSyncClient.DefaultMaxChunk) : IAdbSyncClient
{
    private const int V1HeaderSize = 8;
    private const uint DefaultMaxChunk = 64 * 1024;

    /// <summary>Inbound acceptance ceiling for DATA, DENT name and fail-message lengths; a peer reporting more
    /// is rejected as a protocol violation regardless of the outbound chunk size.
    /// <para>DATA、DENT 名称与失败消息长度的入站接受上限；上报更大值的对端一律按协议违规拒绝，与出站块大小无关。</para></summary>
    internal const uint InboundMaxChunkBytes = 64 * 1024;

    private readonly AdbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly int _outChunk = ResolveOutChunk(maxChunk, connection.PeerMaxPayload);
    private byte[]? _packet;
    private ReadOnlyMemory<byte> _pending;
    private AdbStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Opens the sync stream unless it is already open.
    /// <para>确保 sync 流已打开，需要时打开。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the stream open.<para>取消打开流。</para></param>
    /// <returns>The sync stream.<para>Sync 流。</para></returns>
    /// <exception cref="TimeoutException">The OPEN frame did not reach the transport within its deadline.
    /// <para>OPEN 帧未在期限内抵达传输层。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    private async ValueTask<AdbStream> EnsureStreamAsync(CancellationToken cancellationToken)
    {
        _stream ??= await _connection.OpenProtocolStreamAsync("sync:", cancellationToken).ConfigureAwait(false);
        return _stream;
    }

    /// <summary>
    /// Stats a remote file/directory.
    /// <para>异步查询单个远端文件或目录状态。</para>
    /// </summary>
    /// <param name="remotePath">Device-side path to stat.<para>待查询的设备侧路径。</para></param>
    /// <param name="cancellationToken">Cancels the request or the reply wait.<para>取消请求或应答等待。</para></param>
    /// <returns>A task resolving to the entry, or <see langword="null"/> when the peer reports the path as
    /// absent. <para>结果为条目的任务；对端报告路径不存在时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> is <see langword="null"/>.
    /// <para><paramref name="remotePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The peer rejected the request, answered with an unexpected id, or
    /// reported a message longer than <see cref="InboundMaxChunkBytes"/>.
    /// <para>对端拒绝请求、返回非预期标识，或上报超过 <see cref="InboundMaxChunkBytes"/> 的消息。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask<SyncEntry?> StatAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);

        var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(BuildV1Packet(SyncIds.Stat, Encoding.UTF8.GetBytes(remotePath)), cancellationToken)
            .ConfigureAwait(false);

        var id = Encoding.ASCII.GetString(await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false), 0, 4);

        if (id == SyncIds.Fail)
        {
            var errLength = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
            var err = await ReadExactAsync(stream, CheckedChunkLength(errLength, "STAT fail message"), cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidDataException(
                $"sync STAT failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
        }

        if (id != SyncIds.Stat)
        {
            throw new InvalidDataException($"Unexpected sync response id '{id}' to STAT.");
        }

        var body = await ReadExactAsync(stream, 12, cancellationToken).ConfigureAwait(false);
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        var time = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        var name = Path.GetFileName(remotePath.TrimEnd('/'));
        return new SyncEntry(mode, size, time, name);
    }

    /// <summary>
    /// Lists a remote directory.
    /// <para>异步列出远端目录内容。</para>
    /// </summary>
    /// <param name="remotePath">Device-side directory path.<para>设备侧目录路径。</para></param>
    /// <param name="cancellationToken">Cancels the listing between records and during a read.
    /// <para>在记录之间与读取期间取消列举。</para></param>
    /// <returns>A task resolving to every entry received before the done marker.
    /// <para>结果为结束标记之前收到的全部条目的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> is <see langword="null"/>.
    /// <para><paramref name="remotePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The peer rejected the request, answered with an unexpected id, or
    /// reported a record longer than <see cref="InboundMaxChunkBytes"/>.
    /// <para>对端拒绝请求、返回非预期标识，或上报超过 <see cref="InboundMaxChunkBytes"/> 的记录。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask<IReadOnlyList<SyncEntry>> ListAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);

        var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(BuildV1Packet(SyncIds.List, Encoding.UTF8.GetBytes(remotePath)), cancellationToken)
            .ConfigureAwait(false);

        var entries = new List<SyncEntry>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = Encoding.ASCII.GetString(await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false), 0, 4);

            if (id == SyncIds.Done)
            {
                break;
            }

            if (id == SyncIds.Fail)
            {
                var msglen = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
                var err = await ReadExactAsync(stream, CheckedChunkLength(msglen, "LIST fail message"), cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidDataException(
                    $"sync LIST failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
            }

            if (id != SyncIds.Dent)
            {
                throw new InvalidDataException($"Unexpected sync response id '{id}' to LIST.");
            }

            var body = await ReadExactAsync(stream, 16, cancellationToken).ConfigureAwait(false);
            var mode = BinaryPrimitives.ReadUInt32LittleEndian(body);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            var time = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
            var namelen = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
            var nameBytes = await ReadExactAsync(stream, CheckedChunkLength(namelen, "DENT name"), cancellationToken)
                .ConfigureAwait(false);
            entries.Add(new SyncEntry(mode, size, time, Encoding.UTF8.GetString(nameBytes)));
        }

        return entries;
    }

    /// <summary>
    /// Pushes a local file to the device.
    /// <para>异步将本地文件推送到设备。</para>
    /// </summary>
    /// <param name="localPath">Existing local file opened for reading.<para>以只读方式打开的已存在本地文件。</para></param>
    /// <param name="remotePath">Destination path on the device.<para>设备上的目标路径。</para></param>
    /// <param name="mode">Unix permission bits, sent as octal.<para>以八进制发送的 Unix 权限位。</para></param>
    /// <param name="mtime">Device-side modification timestamp in seconds.<para>设备侧修改时间（秒）。</para></param>
    /// <param name="cancellationToken">Cancels the push between packets.<para>在数据包之间取消推送。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="localPath"/> or
    /// <paramref name="remotePath"/> is <see langword="null"/>.
    /// <para><paramref name="localPath"/> 或 <paramref name="remotePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="localPath"/> does not exist.
    /// <para><paramref name="localPath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The device rejected the transfer or answered with an unexpected id.
    /// <para>设备拒绝传输或返回了非预期标识。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask PushAsync(string localPath, string remotePath, uint mode = 0x81A4, uint mtime = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localPath);
        ArgumentNullException.ThrowIfNull(remotePath);

        await using var fs = File.OpenRead(localPath);
        await PushStreamAsync(fs, remotePath, mode, mtime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes a stream to the device.
    /// <para>异步将流推送到设备。</para>
    /// </summary>
    /// <param name="source">Read to end-of-stream in this client's chunk sizes; ownership stays with the
    /// holder of the stream. <para>按本客户端的块大小读至流末尾；所有权仍归流的原持有者。</para></param>
    /// <param name="remotePath">Destination path on the device.<para>设备上的目标路径。</para></param>
    /// <param name="mode">Unix permission bits, sent as octal.<para>以八进制发送的 Unix 权限位。</para></param>
    /// <param name="mtime">Device-side modification timestamp in seconds; <c>0</c> means the current UTC time.
    /// <para>设备侧修改时间（秒）；<c>0</c> 表示传输时的当前 UTC 时间。</para></param>
    /// <param name="cancellationToken">Cancels the push between packets.<para>在数据包之间取消推送。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or
    /// <paramref name="remotePath"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="remotePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The device rejected the transfer or answered with an unexpected id.
    /// <para>设备拒绝传输或返回了非预期标识。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask PushStreamAsync(Stream source, string remotePath, uint mode = 0x81A4, uint mtime = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(remotePath);

        cancellationToken.ThrowIfCancellationRequested();

        var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false);
        if (mtime == 0)
        {
            mtime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        var sendTarget = $"{remotePath},0{Convert.ToString(mode, 8)}";
        await stream.WriteAsync(BuildV1Packet(SyncIds.Send, Encoding.UTF8.GetBytes(sendTarget)), cancellationToken)
            .ConfigureAwait(false);

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
        }

        var donePacket = new byte[V1HeaderSize];
        Encoding.ASCII.GetBytes(SyncIds.Done, donePacket.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(donePacket.AsSpan(4), mtime);
        await stream.WriteAsync(donePacket, cancellationToken).ConfigureAwait(false);

        await ExpectResponseAsync(stream, "push", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls a remote file into a local file.
    /// <para>异步将设备上的远端文件拉取到本地文件。</para>
    /// </summary>
    /// <param name="remotePath">Device-side source file.<para>设备侧源文件。</para></param>
    /// <param name="localPath">Local destination; created or truncated.<para>本地目标；不存在则创建，存在则截断。</para></param>
    /// <param name="cancellationToken">Cancels the pull between packets.<para>在数据包之间取消拉取。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> or
    /// <paramref name="localPath"/> is <see langword="null"/>.
    /// <para><paramref name="remotePath"/> 或 <paramref name="localPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The device rejected the transfer or answered with an unexpected id.
    /// <para>设备拒绝传输或返回了非预期标识。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);
        ArgumentNullException.ThrowIfNull(localPath);

        await using var fs = File.Create(localPath);
        await PullStreamAsync(remotePath, fs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls a remote file into a stream.
    /// <para>异步将远端文件拉取到流中。</para>
    /// </summary>
    /// <param name="remotePath">Device-side source file.<para>设备侧源文件。</para></param>
    /// <param name="destination">Written sequentially from the current position; ownership stays with the
    /// holder of the stream. <para>从当前位置顺序写入；所有权仍归流的原持有者。</para></param>
    /// <param name="cancellationToken">Cancels the pull between packets.<para>在数据包之间取消拉取。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> or
    /// <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="remotePath"/> 或 <paramref name="destination"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The device rejected the transfer or answered with an unexpected id.
    /// <para>设备拒绝传输或返回了非预期标识。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask PullStreamAsync(string remotePath, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePath);
        ArgumentNullException.ThrowIfNull(destination);

        cancellationToken.ThrowIfCancellationRequested();

        var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(BuildV1Packet(SyncIds.Recv, Encoding.UTF8.GetBytes(remotePath)), cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = await ReadExactAsync(stream, V1HeaderSize, cancellationToken).ConfigureAwait(false);
            var id = Encoding.ASCII.GetString(header, 0, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

            if (id == SyncIds.Done)
            {
                break;
            }

            if (id == SyncIds.Fail)
            {
                var err = await ReadExactAsync(stream, CheckedChunkLength(length, "RECV fail message"), cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidDataException($"sync pull failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
            }

            if (id == SyncIds.Data)
            {
                await CopyExactAsync(destination, stream, CheckedChunkLength(length, "RECV data"), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            throw new InvalidDataException($"Unexpected sync response id '{id}' to RECV.");
        }
    }

    /// <summary>
    /// Sends the QUIT marker and releases the sync stream.
    /// <para>发送 QUIT 标记并释放 sync 流。</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _packet = null;
        _pending = default;

        var stream = _stream;
        _stream = null;

        if (stream is not null && !stream.IsClosed)
        {
            try
            {
                await stream.WriteAsync(BuildV1Packet(SyncIds.Quit, []), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Expects an OKAY or FAIL response from the sync service.
    /// <para>异步等待 sync 服务的 OKAY 或 FAIL 响应。</para>
    /// </summary>
    /// <param name="stream">The sync stream.<para>Sync 流。</para></param>
    /// <param name="operation">The operation name for error messages.<para>用于错误消息的操作名称。</para></param>
    /// <param name="cancellationToken">Cancels the reply wait.<para>取消应答等待。</para></param>
    /// <exception cref="InvalidDataException">The device reported FAIL or answered with an unexpected id.
    /// <para>设备上报 FAIL 或返回了非预期标识。</para></exception>
    private async ValueTask ExpectResponseAsync(AdbStream stream, string operation, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, V1HeaderSize, cancellationToken).ConfigureAwait(false);
        var id = Encoding.ASCII.GetString(header, 0, 4);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

        if (id == SyncIds.Okay)
        {
            return;
        }

        if (id == SyncIds.Fail)
        {
            var err = await ReadExactAsync(stream, CheckedChunkLength(length, $"{operation} fail message"), cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidDataException($"sync {operation} failed: {Encoding.UTF8.GetString(err)}");
        }

        throw new InvalidDataException($"Unexpected sync response id '{id}' during {operation}.");
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer from the sync stream.
    /// <para>异步从 sync 流读取 32 位无符号整数。</para>
    /// </summary>
    /// <param name="stream">The sync stream.<para>Sync 流。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消读取。</para></param>
    /// <returns>The little-endian value.<para>小端取值。</para></returns>
    private async ValueTask<uint> ReadUInt32Async(AdbStream stream, CancellationToken cancellationToken) =>
        BinaryPrimitives.ReadUInt32LittleEndian(await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Builds a v1 sync wire packet (4-byte id + 4-byte length + payload).
    /// <para>构建 v1 sync 线数据包（4 字节 id + 4 字节长度 + 负载）。</para>
    /// </summary>
    /// <param name="id">The 4-byte command id.<para>4 字节命令 id。</para></param>
    /// <param name="payload">The packet payload.<para>数据包负载。</para></param>
    /// <returns>The assembled packet.<para>组装好的数据包。</para></returns>
    private static byte[] BuildV1Packet(string id, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[V1HeaderSize + payload.Length];
        WritePacketHeader(packet, id, payload.Length);
        payload.CopyTo(packet.AsSpan(V1HeaderSize));
        return packet;
    }

    /// <summary>
    /// Writes the 4-byte id and the payload length into a packet buffer that already holds the payload.
    /// <para>把 4 字节 id 与负载长度就地写入已承载负载的数据包缓冲区。</para>
    /// </summary>
    /// <param name="packet">Buffer starting at the sync header.<para>以 sync 头部起始的缓冲区。</para></param>
    /// <param name="id">The 4-byte command id.<para>4 字节命令 id。</para></param>
    /// <param name="payloadLength">Bytes of payload that follow the header.<para>头部之后的负载字节数。</para></param>
    private static void WritePacketHeader(Span<byte> packet, string id, int payloadLength)
    {
        Encoding.ASCII.GetBytes(id, packet[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[4..8], (uint)payloadLength);
    }

    /// <summary>
    /// Reads an exact number of bytes from the sync stream, keeping any excess as a buffer slice instead of a
    /// per-byte queue.
    /// <para>从 sync 流精确读取指定字节数，多余部分以缓冲区切片保留而非逐字节队列。</para>
    /// </summary>
    /// <param name="stream">The sync stream.<para>Sync 流。</para></param>
    /// <param name="length">The number of bytes to read.<para>要读取的字节数。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消读取。</para></param>
    /// <returns>A task resolving to the requested bytes. <para>结果为所请求字节的任务。</para></returns>
    /// <exception cref="InvalidDataException"><paramref name="length"/> is negative.
    /// <para><paramref name="length"/> 为负数。</para></exception>
    /// <exception cref="EndOfStreamException">The stream closed before the request was satisfied.
    /// <para>流在请求满足前已关闭。</para></exception>
    private async ValueTask<byte[]> ReadExactAsync(AdbStream stream, int length, CancellationToken cancellationToken)
    {
        if (length < 0)
        {
            throw new InvalidDataException("Negative sync length.");
        }

        if (length == 0)
        {
            return [];
        }

        if (_pending.Length >= length)
        {
            var slice = _pending[..length];
            _pending = _pending[length..];
            return slice.ToArray();
        }

        var result = new byte[length];
        var offset = 0;

        if (!_pending.IsEmpty)
        {
            _pending.CopyTo(result.AsMemory(offset));
            offset += _pending.Length;
            _pending = default;
        }

        while (offset < length)
        {
            var chunk = await stream.ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Sync stream closed unexpectedly.");
            var needed = length - offset;
            if (chunk.Length <= needed)
            {
                chunk.CopyTo(result.AsMemory(offset));
                offset += chunk.Length;
            }
            else
            {
                chunk[..needed].CopyTo(result.AsMemory(offset));
                _pending = chunk[needed..];
                offset = length;
            }
        }

        return result;
    }

    /// <summary>
    /// Writes an exact number of sync payload bytes to a destination, forwarding inbound chunk slices without
    /// assembling an intermediate copy.
    /// <para>向目标写入恰好数量的 sync 负载字节，直接转交入站块切片而不另外组装中间副本。</para>
    /// </summary>
    /// <param name="destination">Where the payload is appended.<para>负载写入的目标。</para></param>
    /// <param name="stream">The sync stream.<para>Sync 流。</para></param>
    /// <param name="length">The number of payload bytes, already validated.<para>已校验的负载字节数。</para></param>
    /// <param name="cancellationToken">Cancels the copy between chunks.<para>在块之间取消拷贝。</para></param>
    /// <exception cref="EndOfStreamException">The stream closed before the body was complete.
    /// <para>流在数据体结束前已关闭。</para></exception>
    private async ValueTask CopyExactAsync(Stream destination, AdbStream stream, int length,
        CancellationToken cancellationToken)
    {
        var remaining = length;
        while (remaining > 0)
        {
            if (_pending.IsEmpty)
            {
                var chunk = await stream.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Sync stream closed unexpectedly.");
                _pending = chunk;
                continue;
            }

            var take = Math.Min(remaining, _pending.Length);
            await destination.WriteAsync(_pending[..take], cancellationToken).ConfigureAwait(false);
            _pending = _pending[take..];
            remaining -= take;
        }
    }

    /// <summary>
    /// Validates a device-supplied chunk length against the inbound acceptance ceiling; a larger value is an
    /// untrusted-peer protocol violation and must not drive allocation.
    /// <para>按入站接受上限校验设备给出的分块长度；更大的值属于不可信对端的协议违规，不得驱动分配。</para>
    /// </summary>
    /// <param name="length">The length field as reported by the device.<para>设备上报的长度字段。</para></param>
    /// <param name="context">Protocol context used in the error message.<para>用于错误消息的协议上下文。</para></param>
    /// <returns>The validated length as an <see cref="int"/>.<para>校验后的 <see cref="int"/> 长度。</para></returns>
    /// <exception cref="InvalidDataException">The reported length exceeds the inbound ceiling.
    /// <para>上报长度超过入站上限。</para></exception>
    private static int CheckedChunkLength(uint length, string context)
    {
        if (length > InboundMaxChunkBytes)
        {
            throw new InvalidDataException(
                $"Sync {context} length {length} exceeds the {nameof(InboundMaxChunkBytes)} limit {InboundMaxChunkBytes}.");
        }

        return (int)length;
    }

    /// <summary>
    /// Clamps the requested outbound chunk to the inbound acceptance ceiling and to the peer-advertised payload
    /// limit, so one sync packet fits a single WRTE frame.
    /// <para>把请求的出站块钳制到入站接受上限与对端通告的负载上限，使单个 sync 数据包适配一条 WRTE 帧。</para>
    /// </summary>
    /// <param name="requestedMaxChunk">Chunk size requested for the transfer.<para>为该传输请求的分块大小。</para></param>
    /// <param name="peerMaxPayload">Payload limit advertised by the peer; <c>0</c> before the handshake or for an
    /// adb-server-mediated connection.<para>对端通告的负载上限；握手前或经 adb server 的连接为 <c>0</c>。</para></param>
    /// <returns>The outbound chunk size in file bytes.<para>出站数据块的文件字节数。</para></returns>
    private static int ResolveOutChunk(uint requestedMaxChunk, uint peerMaxPayload)
    {
        uint outbound = requestedMaxChunk is 0 or > InboundMaxChunkBytes ? InboundMaxChunkBytes : requestedMaxChunk;
        if (peerMaxPayload is > 0 and < InboundMaxChunkBytes)
        {
            outbound = Math.Min(outbound, peerMaxPayload);
        }

        return checked((int)outbound);
    }
}