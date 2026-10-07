using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

public sealed class AdbSyncCopyTests
{
    [Fact]
    public void DirectStream_WriteBeyondPeerLimit_SplitsWithoutChangingBytes()
    {
        var data = new byte[145];
        new Random(17).NextBytes(data);
        using var transport = new FakeAdbDeviceTransport { PeerMaxPayload = 64 };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        using var stream = (AdbStream)adb.OpenStream("shell:cat");

        stream.Write(data);

        Assert.Equal([64, 64, 17], transport.WrtePayloads.Select(packet => packet.Length));
        Assert.Equal(data, transport.WrtePayloads.SelectMany(packet => packet).ToArray());
    }

    [Fact]
    public async Task DirectStream_WriteAsyncBeyondPeerLimit_SplitsWithoutChangingBytes()
    {
        var data = new byte[145];
        new Random(23).NextBytes(data);
        using var transport = new FakeAdbDeviceTransport { PeerMaxPayload = 64 };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        await adb.ConnectAsync(TestContext.Current.CancellationToken);
        await using var stream = adb.OpenStream("shell:cat");

        await stream.WriteAsync(data, TestContext.Current.CancellationToken);

        Assert.Equal([64, 64, 17], transport.WrtePayloads.Select(packet => packet.Length));
        Assert.Equal(data, transport.WrtePayloads.SelectMany(packet => packet).ToArray());
    }

    [Fact]
    public async Task DirectStream_DelayedOkay_WaitsBeforeNextWrite()
    {
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new FakeAdbDeviceTransport
        {
            PeerMaxPayload = 64,
            AutoAcknowledgeWrites = false,
            OnWrte = _ => firstWrite.TrySetResult()
        };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        using var stream = (AdbStream)adb.OpenStream("shell:cat");
        var writing = stream.WriteAsync(new byte[65], TestContext.Current.CancellationToken).AsTask();

        await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(writing.IsCompleted);
        Assert.Single(transport.WrtePayloads);
        transport.Enqueue(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, 1, stream.LocalId)));
        await writing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([64, 1], transport.WrtePayloads.Select(packet => packet.Length));
    }

    [Fact]
    public async Task DirectStream_ClosedWhileWaitingForOkay_UnblocksWriter()
    {
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new FakeAdbDeviceTransport
        {
            PeerMaxPayload = 64,
            AutoAcknowledgeWrites = false,
            OnWrte = _ => firstWrite.TrySetResult()
        };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        var stream = adb.OpenStream("shell:cat");
        var writing = stream.WriteAsync(new byte[65], TestContext.Current.CancellationToken).AsTask();

        await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        stream.Dispose();

        await Assert.ThrowsAsync<IOException>(() => writing);
        Assert.Single(transport.WrtePayloads);
    }

    [Fact]
    public async Task DirectStream_PeerClose_ClosesLocallyWithoutEcho()
    {
        using var transport = new FakeAdbDeviceTransport();
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        await using var stream = adb.OpenStream("reboot:now");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(5000);
        Assert.Null(await stream.ReadAsync(timeout.Token));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.CloseCount);
    }

    [Theory]
    [InlineData(0, 17)]
    [InlineData(131079, 65536)]
    [InlineData(4099, 17)]
    public async Task PushStreamAsync_ShortReads_EmitsExactIndependentPackets(int length, int maximumRead)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        using var transport = new FakeAdbDeviceTransport();
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        await using var sync = adb.CreateSyncClient();
        await using var source = new ShortReadStream(data, maximumRead);

        await sync.PushStreamAsync(source, "/simulation/file.bin", mtime: 123,
            cancellationToken: TestContext.Current.CancellationToken);

        var offset = 0;
        foreach (var packet in transport.WrtePayloads)
        {
            if (!packet.AsSpan().StartsWith("DATA"u8))
            {
                continue;
            }

            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
            Assert.InRange(count, 1, maximumRead);
            Assert.Equal(8 + count, packet.Length);
            Assert.True(data.AsSpan(offset, count).SequenceEqual(packet.AsSpan(8)));
            offset += count;
        }

        Assert.Equal(length, offset);
        var done = Assert.Single(transport.WrtePayloads, p => p.AsSpan().StartsWith("DONE"u8));
        Assert.Equal(123u, BinaryPrimitives.ReadUInt32LittleEndian(done.AsSpan(4)));
    }

    [Theory]
    [InlineData(0, 17)]
    [InlineData(131079, 65536)]
    [InlineData(4099, 17)]
    public async Task PushStream_ShortReads_EmitsExactIndependentPackets(int length, int maximumRead)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        using var transport = new FakeAdbDeviceTransport();
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        await using var sync = adb.CreateSyncClient();
        using var source = new ShortReadStream(data, maximumRead);

        await sync.PushStreamAsync(source, "/simulation/file.bin", mtime: 123,
            cancellationToken: TestContext.Current.CancellationToken);

        var offset = 0;
        foreach (var packet in transport.WrtePayloads)
        {
            if (!packet.AsSpan().StartsWith("DATA"u8))
            {
                continue;
            }

            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
            Assert.InRange(count, 1, maximumRead);
            Assert.Equal(8 + count, packet.Length);
            Assert.True(data.AsSpan(offset, count).SequenceEqual(packet.AsSpan(8)));
            offset += count;
        }

        Assert.Equal(length, offset);
        var done = Assert.Single(transport.WrtePayloads, p => p.AsSpan().StartsWith("DONE"u8));
        Assert.Equal(123u, BinaryPrimitives.ReadUInt32LittleEndian(done.AsSpan(4)));
    }

    [Fact]
    public async Task PushStreamAsync_CallerCancellation_StopsBetweenPackets()
    {
        using var transport = new FakeAdbDeviceTransport { AutoAcknowledgeWrites = false };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        await using var sync = adb.CreateSyncClient();
        using var source = new MemoryStream(new byte[256 * 1024]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sync.PushStreamAsync(source, "/simulation/file.bin", cancellationToken: cancellation.Token).AsTask());
    }

    [Fact]
    public async Task PullStreamAsync_DataChunks_ReassembleUploadedBytes()
    {
        var data = new byte[200];
        new Random(7).NextBytes(data);
        using var transport = new FakeAdbDeviceTransport { SyncPullData = data, SyncPullChunkSize = 37 };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        await adb.ConnectAsync(TestContext.Current.CancellationToken);
        await using var sync = adb.CreateSyncClient();
        using var destination = new MemoryStream();

        await sync.PullStreamAsync("/simulation/file.bin", destination, TestContext.Current.CancellationToken);

        Assert.Equal(data, destination.ToArray());
    }

    private sealed class ShortReadStream(byte[] bytes, int maximumRead) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, maximumRead));

        public override int Read(Span<byte> destination)
            => base.Read(destination[..Math.Min(destination.Length, maximumRead)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
    }
}