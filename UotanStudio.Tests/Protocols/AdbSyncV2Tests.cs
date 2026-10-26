using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class AdbSyncV2Tests
{
    private const string FullV2Features =
        "device::features=shell_v2,stat_v2,ls_v2,sendrecv_v2,sendrecv_v2_brotli,sendrecv_v2_lz4,sendrecv_v2_zstd,sendrecv_v2_dry_run_send";

    private const string SendRecvOnlyFeatures = "device::features=sendrecv_v2";

    private const string NoV2Features = "device::features=shell_v2";

    [Fact(Timeout = 15000)]
    public async Task Stat64Async_StatV2Device_ReturnsFullRecord()
    {
        var transport = new ScriptedAdbTransport(FullV2Features);
        transport.EnqueueResponse(PayloadFrame(StatV2Body(error: 0, mode: 0x81A4, size: 0x1_0000_0005UL, mtime: 1_700_000_000)));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();

        var entry = await sync.Stat64Async("/sdcard/file.bin", TestContext.Current.CancellationToken);

        transport.Complete();
        Assert.NotNull(entry);
        Assert.Equal(0x81A4u, entry.Value.Mode);
        Assert.Equal(0x1_0000_0005UL, entry.Value.Size);
        Assert.Equal(1_700_000_000L, entry.Value.Mtime);
        Assert.Equal("file.bin", entry.Value.Name);
        var payloads = ClientSyncPayloads(transport);
        Assert.Contains(payloads, p => p.AsSpan(0, 4).SequenceEqual("STA2"u8) &&
            Encoding.UTF8.GetString(p[8..]).Contains("/sdcard/file.bin", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15000)]
    public async Task Stat64Async_StatV2Error_ReturnsNull()
    {
        var transport = new ScriptedAdbTransport(FullV2Features);
        transport.EnqueueResponse(PayloadFrame(StatV2Body(error: 2, mode: 0, size: 0, mtime: 0)));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();

        var entry = await sync.Stat64Async("/sdcard/missing", TestContext.Current.CancellationToken);

        Assert.Null(entry);
    }

    [Fact(Timeout = 15000)]
    public async Task Stat64Async_WithoutStatV2_FallsBackToV1()
    {
        var transport = new ScriptedAdbTransport(NoV2Features);
        var v1Body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(v1Body.AsSpan(0), 0x81A4);
        BinaryPrimitives.WriteUInt32LittleEndian(v1Body.AsSpan(4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(v1Body.AsSpan(8), 1_700_000_000);
        transport.EnqueueResponse(PayloadFrame([.. "STAT"u8, .. v1Body]));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();

        var entry = await sync.Stat64Async("/sdcard/file.bin", TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal(4096u, entry.Value.Size);
        Assert.Equal(0UL, entry.Value.Dev);
        var payloads = ClientSyncPayloads(transport);
        Assert.Contains(payloads, p => p.AsSpan(0, 4).SequenceEqual("STAT"u8));
    }

    [Fact(Timeout = 15000)]
    public async Task List64Async_ListV2_ParsesEntriesAndSkipsFailedStat()
    {
        var transport = new ScriptedAdbTransport(FullV2Features);
        transport.EnqueueResponse(
            PayloadFrame(DentV2("a.txt", error: 0, mode: 0x81A4, size: 0x1_0000_0002UL)),
            PayloadFrame(DentV2("broken", error: 2, mode: 0, size: 0)),
            PayloadFrame(ListDoneMarker()));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();

        var entries = await sync.List64Async("/sdcard", TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries);
        Assert.Equal("a.txt", entry.Name);
        Assert.Equal(0x1_0000_0002UL, entry.Size);
        var payloads = ClientSyncPayloads(transport);
        Assert.Contains(payloads, p => p.AsSpan(0, 4).SequenceEqual("LIS2"u8));
    }

    [Fact(Timeout = 15000)]
    public async Task PullStreamAsync_WithoutSendRecvV2_UsesV1Recv()
    {
        var transport = new ScriptedAdbTransport(NoV2Features);
        transport.EnqueueResponse(PayloadFrame(SyncDataPacket([1, 2, 3, 4])), PayloadFrame(SyncDonePacket()));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();
        var destination = new MemoryStream();

        await sync.PullStreamAsync("/sdcard/file.bin", destination, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4], destination.ToArray());
        var payloads = ClientSyncPayloads(transport);
        Assert.Contains(payloads, p => p.AsSpan(0, 4).SequenceEqual("RECV"u8));
    }

    [Fact(Timeout = 15000)]
    public async Task PullStreamAsync_RawV2_StreamsUncompressedBlocks()
    {
        var transport = new ScriptedAdbTransport(SendRecvOnlyFeatures);
        transport.EnqueueResponse(PayloadFrame(SyncDataPacket([5, 6, 7, 8])), PayloadFrame(SyncDonePacket()));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();
        var destination = new MemoryStream();

        await sync.PullStreamAsync("/sdcard/file.bin", destination, TestContext.Current.CancellationToken);

        Assert.Equal([5, 6, 7, 8], destination.ToArray());
        var payloads = ClientSyncPayloads(transport);
        var request = Assert.Single(payloads, p => p.AsSpan(0, 4).SequenceEqual("RCV2"u8));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8 + "/sdcard/file.bin".Length + 4)));
    }

    [Theory(Timeout = 15000)]
    [InlineData(AdbSyncCompression.Brotli)]
    [InlineData(AdbSyncCompression.Lz4)]
    [InlineData(AdbSyncCompression.Zstd)]
    public async Task PullStreamAsync_CompressedCodec_DecodesContinuousStream(AdbSyncCompression codec)
    {
        var raw = new byte[96_000];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i % 7);
        }

        var compressed = Compress(codec, raw);
        Assert.True(compressed.Length < 64 * 1024, "test payload must fit one sync DATA block");
        var transport = new ScriptedAdbTransport(FullV2Features);
        transport.EnqueueResponse(PayloadFrame(SyncDataPacket(compressed)), PayloadFrame(SyncDonePacket()));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();
        var destination = new MemoryStream();

        await sync.PullStreamAsync("/sdcard/file.bin", destination, TestContext.Current.CancellationToken, codec);

        Assert.Equal(raw, destination.ToArray());
    }

    [Fact(Timeout = 15000)]
    public async Task PullStreamAsync_FailFrame_SurfacesDeviceMessage()
    {
        var transport = new ScriptedAdbTransport(SendRecvOnlyFeatures);
        transport.EnqueueResponse(PayloadFrame([.. "FAIL"u8, .. Le32(5), .. "gone!"u8]));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();
        var destination = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            sync.PullStreamAsync("/sdcard/file.bin", destination, TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Contains("gone!", failure.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15000)]
    public async Task PushStreamAsync_CompressedCodec_EncodesContinuousStream()
    {
        var raw = new byte[96_000];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i % 5);
        }

        var transport = new ScriptedAdbTransport(FullV2Features);
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();
        var source = new MemoryStream(raw);

        await sync.PushStreamAsync(source, "/sdcard/file.bin", mtime: 1_700_000_000,
            cancellationToken: TestContext.Current.CancellationToken, compression: AdbSyncCompression.Zstd);

        var payloads = ClientSyncPayloads(transport);
        var setup = payloads.FirstOrDefault(p => p.AsSpan(0, 4).SequenceEqual("SND2"u8));
        Assert.NotNull(setup);
        var pathLength = BinaryPrimitives.ReadUInt32LittleEndian(setup.AsSpan(4, 4));
        Assert.Equal("/sdcard/file.bin", Encoding.UTF8.GetString(setup.AsSpan(8, (int)pathLength)));
        Assert.Equal(0x81A4u, BinaryPrimitives.ReadUInt32LittleEndian(setup.AsSpan(12 + (int)pathLength, 4)));
        Assert.Equal(SyncFlagsZstd, BinaryPrimitives.ReadUInt32LittleEndian(setup.AsSpan(16 + (int)pathLength, 4)));
        var compressed = payloads
            .Where(p => p.AsSpan(0, 4).SequenceEqual("DATA"u8))
            .SelectMany(p => p[8..])
            .ToArray();
        Assert.Equal(raw, Decompress(AdbSyncCompression.Zstd, compressed));
        var done = payloads.Last(p => p.AsSpan(0, 4).SequenceEqual("DONE"u8));
        Assert.Equal(1_700_000_000u, BinaryPrimitives.ReadUInt32LittleEndian(done.AsSpan(4, 4)));
    }

    [Fact(Timeout = 15000)]
    public async Task PushStreamAsync_DryRun_SetsDryRunFlag()
    {
        var transport = new ScriptedAdbTransport(FullV2Features);
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        await using var sync = session.CreateSyncClient();
        var source = new MemoryStream([1, 2, 3]);

        await sync.PushStreamAsync(source, "/sdcard/file.bin", cancellationToken: TestContext.Current.CancellationToken,
            dryRun: true);

        var payloads = ClientSyncPayloads(transport);
        var setup = Assert.Single(payloads, p => p.AsSpan(0, 4).SequenceEqual("SND2"u8));
        var pathLength = BinaryPrimitives.ReadUInt32LittleEndian(setup.AsSpan(4, 4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(setup.AsSpan(16 + (int)pathLength, 4));
        Assert.Equal(0x8000_0000u, flags & 0x8000_0000u);
    }

    private const uint SyncFlagsZstd = 4;

    private static byte[] Cnxn(string banner) =>
        AdbMessaging.Serialize(new AdbMessage(AdbCommand.Cnxn, AdbConstants.ProtocolVersion,
            AdbConstants.MaxPayload, Encoding.UTF8.GetBytes(banner)));

    private static byte[] OkayFrame(uint remoteId, uint localId) =>
        AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, remoteId, localId, []));

    private static byte[] PayloadFrame(byte[] payload) =>
        AdbMessaging.Serialize(new AdbMessage(AdbCommand.Wrte, 100, 1, payload));

    private static byte[] Le32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] SyncDataPacket(byte[] payload) =>
        [.. "DATA"u8, .. Le32((uint)payload.Length), .. payload];

    private static byte[] SyncDonePacket() =>
        [.. "DONE"u8, .. Le32(0)];

    private static byte[] StatV2Body(uint error, uint mode, ulong size, long mtime)
    {
        var body = new byte[72];
        "STA2"u8.CopyTo(body);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), error);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), 0x1234);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16), 0x5678);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(36), 1001);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(40), size);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(48), mtime - 10);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(56), mtime);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(64), mtime + 10);
        return body;
    }

    private static byte[] DentV2(string name, uint error, uint mode, ulong size)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var record = new byte[76 + nameBytes.Length];
        "DNT2"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), error);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), mode);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), size);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(72), (uint)nameBytes.Length);
        nameBytes.CopyTo(record.AsSpan(76));
        return record;
    }

    private static byte[] ListDoneMarker()
    {
        var marker = new byte[76];
        "DONE"u8.CopyTo(marker);
        return marker;
    }

    /// <summary>
    /// Reassembles sync payloads from captured client writes; the connection writes each frame's
    /// 24-byte header and its payload as two separate transport writes.
    /// <para>从捕获的客户端写重组 sync 负载；连接把每帧的 24 字节帧头与负载分成两次传输写。</para>
    /// </summary>
    private static List<byte[]> ClientSyncPayloads(ScriptedAdbTransport transport)
    {
        var frames = transport.Writes;
        var payloads = new List<byte[]>();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Length != 24 || !frame.AsSpan(0, 4).SequenceEqual("WRTE"u8))
            {
                continue;
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12, 4));
            if (length == 0)
            {
                payloads.Add([]);
                continue;
            }

            if (i + 1 < frames.Count && frames[i + 1].Length == length)
            {
                payloads.Add(frames[i + 1]);
                i++;
            }
        }

        return payloads;
    }

    private static byte[] Compress(AdbSyncCompression codec, byte[] raw)
    {
        using var output = new MemoryStream();
        switch (codec)
        {
            case AdbSyncCompression.Brotli:
                using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                {
                    brotli.Write(raw);
                }

                break;
            case AdbSyncCompression.Lz4:
                using (var lz4 = LZ4Stream.Encode(output, LZ4Level.L00_FAST, 0, leaveOpen: true))
                {
                    lz4.Write(raw);
                }

                break;
            case AdbSyncCompression.Zstd:
                using (var zstd = new ZstdSharp.CompressionStream(output, leaveOpen: true))
                {
                    zstd.Write(raw);
                }

                break;
        }

        return output.ToArray();
    }

    private static byte[] Decompress(AdbSyncCompression codec, byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var output = new MemoryStream();
        switch (codec)
        {
            case AdbSyncCompression.Brotli:
                using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
                {
                    brotli.CopyTo(output);
                }

                break;
            case AdbSyncCompression.Lz4:
                using (var lz4 = LZ4Stream.Decode(input, leaveOpen: true))
                {
                    lz4.CopyTo(output);
                }

                break;
            case AdbSyncCompression.Zstd:
                using (var zstd = new ZstdSharp.DecompressionStream(input, leaveOpen: true))
                {
                    zstd.CopyTo(output);
                }

                break;
        }

        return output.ToArray();
    }

    /// <summary>
    /// Reactive fake device: answers every client OPEN and WRTE frame with the matching OKAY
    /// acknowledgement and, for each acknowledged sync request, releases the next scripted response
    /// burst onto the read queue.
    /// <para>响应式假设备：对客户端的每条 OPEN 与 WRTE 帧回匹配的 OKAY 确认，并对每条被确认的 sync 请求
    /// 把下一组脚本化响应帧释放到读队列。</para>
    /// </summary>
    private sealed class ScriptedAdbTransport : ICommTransport
    {
        private readonly string _banner;
        private readonly Lock _gate = new();
        private readonly Queue<byte[]> _chunks = new();
        private readonly Queue<byte[][]> _responseBursts = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly List<byte[]> _writes = [];
        private byte[]? _pending;
        private int _pendingOffset;
        private bool _eof;

        public ScriptedAdbTransport(string banner)
        {
            _banner = banner;
            Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Cnxn,
                AdbConstants.ProtocolVersion, AdbConstants.MaxPayload, Encoding.UTF8.GetBytes(_banner))));
        }

        public bool IsConnected => !_eof;

        public IReadOnlyList<byte[]> Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public void EnqueueResponse(params byte[][] frames)
        {
            lock (_gate)
            {
                _responseBursts.Enqueue(frames);
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                _eof = true;
            }

            _available.Release();
        }

        public void Connect()
        {
        }

        public void Disconnect() => Complete();

        public void Reset()
        {
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (TryReadLocked(buffer, offset, length, out var read))
                    {
                        return read;
                    }

                    if (_eof)
                    {
                        return 0;
                    }
                }

                if (timeoutMs > 0)
                {
                    if (!await _available.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
                            .ConfigureAwait(false))
                    {
                        throw new TimeoutException("No ADB bytes arrived.");
                    }
                }
                else
                {
                    await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => ReadAsync(buffer, offset, length, timeoutMs, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public long Write(byte[] data, int offset, int length, int timeoutMs)
        {
            Ack(data[offset..(offset + length)]);
            return length;
        }

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            Ack(data[offset..(offset + length)]);
            return Task.FromResult<long>(length);
        }

        private void Ack(byte[] frame)
        {
            lock (_gate)
            {
                _writes.Add(frame);
            }

            if (frame.Length < 24)
            {
                return;
            }

            var command = Encoding.ASCII.GetString(frame, 0, 4);
            var arg0 = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4));
            var arg1 = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8, 4));
            if (command == "OPEN")
            {
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, 100, arg0, [])));
                return;
            }

            if (command == "CLSE")
            {
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Clse, arg1, arg0, [])));
                Complete();
                return;
            }

            if (command != "WRTE")
            {
                return;
            }

            Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, 100, arg0, [])));
            lock (_gate)
            {
                if (_responseBursts.Count > 0)
                {
                    foreach (var response in _responseBursts.Dequeue())
                    {
                        DeliverLocked(response);
                    }
                }
            }
        }

        private void Deliver(byte[] chunk)
        {
            lock (_gate)
            {
                DeliverLocked(chunk);
            }
        }

        private void DeliverLocked(byte[] chunk)
        {
            _chunks.Enqueue(chunk);
            _available.Release();
        }

        private bool TryReadLocked(byte[] buffer, int offset, int length, out int read)
        {
            if (_pending is null)
            {
                if (_chunks.Count == 0)
                {
                    read = 0;
                    return false;
                }

                _pending = _chunks.Dequeue();
                _pendingOffset = 0;
            }

            read = Math.Min(length, _pending.Length - _pendingOffset);
            _pending.AsSpan(_pendingOffset, read).CopyTo(buffer.AsSpan(offset, read));
            _pendingOffset += read;
            if (_pendingOffset >= _pending.Length)
            {
                _pending = null;
            }

            return true;
        }

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose() => Complete();
    }

    [Fact(Timeout = 15000)]
    public async Task MessageLoop_BudgetExceededStream_SendsCloseAndStopsAcknowledging()
    {
        var transport = new ScriptedAdbTransport(NoV2Features);
        var burst = new List<byte[]> { PayloadFrame([.. "STAT"u8, .. Le32(12), .. new byte[12]]) };
        for (var i = 0; i < 4200; i++)
        {
            burst.Add(PayloadFrame(new byte[8]));
        }

        transport.EnqueueResponse([.. burst]);
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        var sync = session.CreateSyncClient();

        _ = await sync.StatAsync("/sdcard/file.bin", TestContext.Current.CancellationToken);
        var closeSeen = false;
        for (var attempt = 0; attempt < 200 && !closeSeen; attempt++)
        {
            closeSeen = transport.Writes.Any(frame => frame.Length >= 4 && frame.AsSpan(0, 4).SequenceEqual("CLSE"u8));
            if (!closeSeen)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(closeSeen, "the connection must CLSE a stream whose receive budget was exceeded");
        await sync.DisposeAsync();
    }
}