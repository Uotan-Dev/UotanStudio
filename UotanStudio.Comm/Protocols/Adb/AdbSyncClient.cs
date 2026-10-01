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
/// (SEND / RECV / STAT / LIST) aligned with AOSP file_sync_protocol.h.
/// Speaks the v1 wire format, which adbd accepts even when sendrecv_v2 is negotiated.
/// <para>ADB sync 服务客户端，实现文件传输（SEND / RECV / STAT / LIST）。</para>
/// </summary>
/// <remarks>
/// Initializes a new sync client.
/// <para>初始化新的 sync 客户端。</para>
/// </remarks>
internal sealed class AdbSyncClient(AdbConnection connection, uint maxChunk = AdbSyncClient.DefaultMaxChunk) : IAdbSyncClient
{
    private const int V1HeaderSize = 8;
    private const uint DefaultMaxChunk = 64 * 1024;

    private readonly AdbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly uint _maxChunk = maxChunk;
    private byte[]? _pendingChunk;
    private int _pendingOffset;
    private int _pendingCount;
    private AdbStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Ensures the sync stream is open, opening it if needed.
    /// <para>确保 sync 流已打开，需要时打开。</para>
    /// </summary>
    private AdbStream EnsureStream()
    {
        _stream ??= _connection.OpenStream("sync:");

        return _stream;
    }

    /// <summary>
    /// Stats a remote file/directory.
    /// <para>查询单个远端文件或目录状态。</para>
    /// </summary>
    public SyncEntry? Stat(string remotePath)
    {
        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        var stream = EnsureStream();

        var request = BuildV1Packet(SyncIds.Stat, Encoding.UTF8.GetBytes(remotePath));
        stream.Write(request);

        var id = Encoding.ASCII.GetString(ReadExact(stream, 4), 0, 4);

        if (id == SyncIds.Fail)
        {
            var errLength = ReadUInt32(stream);
            var err = ReadExact(stream, CheckedChunkLength(errLength, "STAT fail message"));
            throw new InvalidDataException(
                $"sync STAT failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
        }

        if (id != SyncIds.Stat)
        {
            throw new InvalidDataException($"Unexpected sync response id '{id}' to STAT.");
        }

        var body = ReadExact(stream, 12);
        var mode = BitConverter.ToUInt32(body, 0);
        var size = BitConverter.ToUInt32(body, 4);
        var time = BitConverter.ToUInt32(body, 8);
        var name = Path.GetFileName(remotePath.TrimEnd('/'));
        return new SyncEntry(mode, size, time, name);
    }

    /// <summary>
    /// Lists a remote directory.
    /// <para>列出远端目录内容。</para>
    /// </summary>
    public IReadOnlyList<SyncEntry> List(string remotePath, CancellationToken cancellationToken = default)
    {
        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        var stream = EnsureStream();

        var request = BuildV1Packet(SyncIds.List, Encoding.UTF8.GetBytes(remotePath));
        stream.Write(request);

        var entries = new List<SyncEntry>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = Encoding.ASCII.GetString(ReadExact(stream, 4), 0, 4);

            if (id == SyncIds.Done)
            {
                break;
            }

            if (id == SyncIds.Fail)
            {
                var msglen = ReadUInt32(stream);
                var err = ReadExact(stream, CheckedChunkLength(msglen, "LIST fail message"));
                throw new InvalidDataException(
                    $"sync LIST failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
            }

            if (id != SyncIds.Dent)
            {
                throw new InvalidDataException($"Unexpected sync response id '{id}' to LIST.");
            }

            var body = ReadExact(stream, 16);
            var mode = BitConverter.ToUInt32(body, 0);
            var size = BitConverter.ToUInt32(body, 4);
            var time = BitConverter.ToUInt32(body, 8);
            var namelen = BitConverter.ToUInt32(body, 12);
            var nameBytes = ReadExact(stream, CheckedChunkLength(namelen, "DENT name"));
            entries.Add(new SyncEntry(mode, size, time, Encoding.UTF8.GetString(nameBytes)));
        }

        return entries;
    }

    /// <summary>
    /// Pushes a local file to the device.
    /// <para>将本地文件推送到设备。</para>
    /// </summary>
    public void Push(string localPath, string remotePath, uint mode = 0x81A4, uint mtime = 0, CancellationToken cancellationToken = default)
    {
        if (localPath is null)
        {
            throw new ArgumentNullException(nameof(localPath));
        }

        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        using var fs = File.OpenRead(localPath);
        PushStream(fs, remotePath, mode, mtime, cancellationToken);
    }

    /// <summary>
    /// Pushes a stream to the device.
    /// <para>将流推送到设备。</para>
    /// </summary>
    public void PushStream(Stream source, string remotePath, uint mode = 0x81A4, uint mtime = 0, CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stream = EnsureStream();
        if (mtime == 0)
        {
            mtime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        var sendTarget = $"{remotePath},0{Convert.ToString(mode, 8)}";
        var sendRequest = BuildV1Packet(SyncIds.Send, Encoding.UTF8.GetBytes(sendTarget));
        stream.Write(sendRequest);

        var buffer = new byte[_maxChunk];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var dataPacket = BuildV1Packet(SyncIds.Data, buffer.AsSpan(0, read));
            stream.Write(dataPacket);
        }

        var donePacket = new byte[V1HeaderSize];
        Encoding.ASCII.GetBytes(SyncIds.Done).CopyTo(donePacket, 0);
        BitConverter.GetBytes(mtime).CopyTo(donePacket, 4);
        stream.Write(donePacket);

        ExpectResponse(stream, "push");
    }

    /// <summary>
    /// Pulls a remote file into a local file.
    /// <para>将设备上的远端文件拉取到本地文件。</para>
    /// </summary>
    public void Pull(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        if (localPath is null)
        {
            throw new ArgumentNullException(nameof(localPath));
        }

        using var fs = File.Create(localPath);
        PullStream(remotePath, fs, cancellationToken);
    }

    /// <summary>Pulls a remote file into a stream. 将远端文件拉取到流中。</summary>
    public void PullStream(string remotePath, Stream destination, CancellationToken cancellationToken = default)
    {
        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stream = EnsureStream();
        var request = BuildV1Packet(SyncIds.Recv, Encoding.UTF8.GetBytes(remotePath));
        stream.Write(request);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = ReadExact(stream, V1HeaderSize);
            var id = Encoding.ASCII.GetString(header, 0, 4);
            var length = BitConverter.ToUInt32(header, 4);

            if (id == SyncIds.Done)
            {
                break;
            }

            if (id == SyncIds.Fail)
            {
                var err = ReadExact(stream, CheckedChunkLength(length, "RECV fail message"));
                throw new InvalidDataException($"sync pull failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
            }

            if (id == SyncIds.Data)
            {
                var body = ReadExact(stream, CheckedChunkLength(length, "RECV data"));
                destination.Write(body, 0, body.Length);
                continue;
            }

            throw new InvalidDataException($"Unexpected sync response id '{id}' to RECV.");
        }
    }

    /// <summary>Closes the sync service stream. 关闭 sync 服务流。</summary>
    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _pendingChunk = null;
        _pendingOffset = 0;
        _pendingCount = 0;

        var stream = _stream;
        _stream = null;

        if (stream is not null && !stream.IsClosed)
        {
            try
            {
                var quit = BuildV1Packet(SyncIds.Quit, []);
                stream.Write(quit);
            }
            catch { }
            stream.Close();
        }
    }

    /// <summary>
    /// Releases all resources used by the sync client.
    /// <para>释放 sync 客户端使用的所有资源。</para>
    /// </summary>
    public void Dispose() { Close(); GC.SuppressFinalize(this); }

    /// <summary>
    /// Expects an OKAY or FAIL response from the sync service.
    /// <para>等待 sync 服务的 OKAY 或 FAIL 响应。</para>
    /// </summary>
    /// <param name="stream">The sync stream. Sync 流。</param>
    /// <param name="operation">The operation name for error messages. 用于错误消息的操作名称。</param>
    private void ExpectResponse(AdbStream stream, string operation)
    {
        var header = ReadExact(stream, V1HeaderSize);
        var id = Encoding.ASCII.GetString(header, 0, 4);
        var length = BitConverter.ToUInt32(header, 4);

        if (id == SyncIds.Okay)
        {
            return;
        }

        if (id == SyncIds.Fail)
        {
            var err = ReadExact(stream, CheckedChunkLength(length, $"{operation} fail message"));
            throw new InvalidDataException($"sync {operation} failed: {Encoding.UTF8.GetString(err)}");
        }

        throw new InvalidDataException($"Unexpected sync response id '{id}' during {operation}.");
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer from the sync stream.
    /// <para>从 sync 流读取 32 位无符号整数。</para>
    /// </summary>
    /// <param name="stream">The sync stream. Sync 流。</param>
    private uint ReadUInt32(AdbStream stream) =>
        BitConverter.ToUInt32(ReadExact(stream, 4), 0);

    /// <summary>
    /// Builds a v1 sync wire packet (4-byte id + 4-byte length + payload).
    /// <para>构建 v1 sync 线数据包（4 字节 id + 4 字节长度 + 负载）。</para>
    /// </summary>
    /// <param name="id">The 4-byte command id. 4 字节命令 id。</param>
    /// <param name="payload">The packet payload. 数据包负载。</param>
    private static byte[] BuildV1Packet(string id, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[V1HeaderSize + payload.Length];
        Encoding.ASCII.GetBytes(id, packet.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(packet.AsSpan(V1HeaderSize));
        return packet;
    }

    /// <summary>
    /// Reads an exact number of bytes from the sync stream, buffering any excess. The excess is
    /// retained as a chunk slice instead of a per-byte queue, so a 64 KiB DATA chunk followed by a
    /// small request does not degenerate into per-byte enqueue/dequeue work.
    /// <para>从 sync 流读取精确数量的字节，多余部分入缓冲。多余部分以分块切片保存而非逐字节队列，
    /// 因此 64 KiB DATA 分块之后的小请求不会退化为逐字节的入队/出队操作。</para>
    /// </summary>
    /// <param name="stream">The sync stream. Sync 流。</param>
    /// <param name="length">The number of bytes to read. 要读取的字节数。</param>
    private byte[] ReadExact(AdbStream stream, int length)
    {
        if (length < 0)
        {
            throw new InvalidDataException("Negative sync length.");
        }

        var result = new byte[length];
        var offset = 0;

        while (offset < length && _pendingCount > 0)
        {
            var take = Math.Min(length - offset, _pendingCount);
            Buffer.BlockCopy(_pendingChunk!, _pendingOffset, result, offset, take);
            _pendingOffset += take;
            _pendingCount -= take;
            offset += take;
            if (_pendingCount == 0)
            {
                _pendingChunk = null;
                _pendingOffset = 0;
            }
        }

        while (offset < length)
        {
            var chunk = stream.Read() ?? throw new EndOfStreamException("Sync stream closed unexpectedly.");
            var needed = length - offset;
            if (chunk.Length <= needed)
            {
                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            else
            {
                Buffer.BlockCopy(chunk, 0, result, offset, needed);
                offset += needed;
                _pendingChunk = chunk;
                _pendingOffset = needed;
                _pendingCount = chunk.Length - needed;
            }
        }

        return result;
    }

    /// <summary>
    /// Validates a device-supplied chunk length against the negotiated maximum. The sync v1 wire
    /// protocol never reports a payload or name longer than the sync chunk size, so a larger value
    /// is a protocol violation from an untrusted peer and must fail instead of driving an
    /// attacker-sized allocation.
    /// <para>按协商上限校验设备给出的分块长度。sync v1 线上协议上报的负载或名称不会超过 sync
    /// 分块大小，更大的值属于不可信对端的协议违规，必须失败而不是驱动攻击者指定大小的分配。</para>
    /// </summary>
    /// <param name="length">The length field as reported by the device. 设备上报的长度字段。</param>
    /// <param name="context">Protocol context used in the error message. 用于错误消息的协议上下文。</param>
    /// <returns>The validated length as an <see cref="int"/>. 校验后的 <see cref="int"/> 长度。</returns>
    /// <exception cref="InvalidDataException">The reported length exceeds the negotiated maximum chunk size.
    /// <para>上报长度超过协商的最大分块大小。</para></exception>
    private int CheckedChunkLength(uint length, string context)
    {
        if (length > _maxChunk)
        {
            throw new InvalidDataException($"Sync {context} length {length} exceeds the {nameof(_maxChunk)} limit {_maxChunk}.");
        }

        return (int)length;
    }
}