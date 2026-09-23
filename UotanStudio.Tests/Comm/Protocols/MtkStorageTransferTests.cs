using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXFlashStorageKind.Emmc, 8u)]
    [InlineData(MtkXFlashStorageKind.Ufs, 3u)]
    public async Task ReadXFlashStorageAsync_MultiplePackets_UsesExactRangeAndAcknowledgesData(MtkXFlashStorageKind kind, uint partition)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport, kind);
        var bytes = Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray();
        EnqueueTransferStart(transport, read: true);
        EnqueueXFlashFrame(transport, bytes[..512]);
        EnqueueXFlashFrame(transport, bytes[512..]);
        EnqueueXFlashFrame(transport, new byte[4]);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await session.ReadXFlashStorageAsync(partition, 512, 1024, destination, progress, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(new long[] { 512, 1024 }, progress.Values);
        Assert.Equal(Convert.FromHexString("05000100"), transport.Writes[before + 1]);
        var parameters = transport.Writes[before + 3];
        Assert.Equal(56, parameters.Length);
        Assert.Equal(kind == MtkXFlashStorageKind.Emmc ? 1u : 0x30u, BinaryPrimitives.ReadUInt32LittleEndian(parameters));
        Assert.Equal(partition, BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(4)));
        Assert.Equal(512ul, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(8)));
        Assert.Equal(1024ul, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(16)));
        Assert.All(parameters.Skip(24), value => Assert.Equal(0, value));
        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(new byte[4], transport.Writes[before + 5]);
        Assert.Equal(new byte[4], transport.Writes[before + 7]);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashStorageAsync_MultiplePackets_SendsChecksumAndExactDataBeforeCompletion()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        var bytes = Enumerable.Range(0, 1024).Select(index => (byte)(index * 7)).ToArray();
        using var source = new FragmentedSource(bytes);
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await session.WriteXFlashStorageAsync(8, 0, 1024, source, progress, TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("04000100"), transport.Writes[before + 1]);
        for (var index = 0; index < 2; index++)
        {
            var packet = bytes.AsSpan(index * 512, 512).ToArray();
            var packetWrites = before + 4 + index * 6;
            Assert.Equal(new byte[4], transport.Writes[packetWrites + 1]);
            Assert.Equal((uint)(packet.Sum(value => (int)value) & 0xFFFF),
                BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[packetWrites + 3]));
            Assert.Equal(packet, transport.Writes[packetWrites + 5]);
        }
        Assert.Equal(16, transport.Writes.Count - before);
        Assert.Equal(new long[] { 512, 1024 }, progress.Values);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData("early-status")]
    [InlineData("oversized")]
    [InlineData("unaligned")]
    [InlineData("wrong-type")]
    public async Task ReadXFlashStorageAsync_InvalidDataFrame_RejectsWithoutAcknowledging(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: true);
        var payload = failure switch
        {
            "early-status" => new byte[] { 1, 0, 0, 0xC0 },
            "oversized" => new byte[1024],
            "unaligned" => new byte[511],
            _ => new byte[512]
        };
        EnqueueXFlashFrame(transport, payload, failure == "wrong-type" ? 2u : 1u);
        var before = transport.Writes.Count;
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashStorageAsync(
            8, 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, destination.Length);
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageTransferAsync_FinalRejection_DoesNotReportSuccess(bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: !write);
        EnqueueXFlashFrame(transport, write ? new byte[4] : new byte[512]);
        EnqueueXFlashFrame(transport, [4, 0, 7, 0xC0]);
        using var stream = new MemoryStream(new byte[512]);

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => write
            ? session.WriteXFlashStorageAsync(8, 0, 512, stream, cancellationToken: TestContext.Current.CancellationToken)
            : session.ReadXFlashStorageAsync(8, 0, 512, stream, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0xC0070004u, error.Status);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(8u, 4096L, 512L)]
    [InlineData(8u, 1L, 512L)]
    [InlineData(3u, 0L, 512L)]
    public async Task StorageTransferAsync_InvalidRange_RejectsBeforeIo(uint partition, long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var stream = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<Exception>(() => session.ReadXFlashStorageAsync(
            partition, offset, length, stream, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => session.WriteXFlashStorageAsync(
            partition, offset, length, stream, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashStorageAsync_TruncatedSource_DoesNotPadOrSendNextPacket()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashFrame(transport, new byte[4]);
        using var source = new NonSeekableStorageSource(new byte[600]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.WriteXFlashStorageAsync(
            8, 0, 1024, source, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(10, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private static void EnqueueTransferStart(ScriptedCommTransport transport, bool read)
    {
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        if (read)
        {
            EnqueueXFlashFrame(transport, new byte[4]);
        }
    }

    [Fact]
    public async Task WriteXFlashStorageAsync_PacketRejected_StopsBeforeReadingNextPacket()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashFrame(transport, [1, 0, 0, 0xC0]);
        using var source = new MemoryStream(new byte[1024]);
        var progress = new ProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteXFlashStorageAsync(
            8, 0, 1024, source, progress, TestContext.Current.CancellationToken));

        Assert.Equal(512, source.Position);
        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task StorageTransferAsync_CancelledQueuedRead_DoesNotInterruptActiveWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        using var source = new PausedStorageSource();
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var write = session.WriteXFlashStorageAsync(8, 0, 512, source, cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        try
        {
            var read = session.ReadXFlashStorageAsync(8, 0, 512, destination, cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            source.Resume.TrySetResult();
            await write;
        }
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    private sealed class PausedStorageSource : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PausedStorageSource() : base(new byte[512]) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static async Task<MtkSession> CreateStorageSessionAsync(ScriptedCommTransport transport,
        MtkXFlashStorageKind kind = MtkXFlashStorageKind.Emmc)
    {
        var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, StorageGeometry(kind));
        EnqueueControlReply(transport, Convert.FromHexString("0002000000020000"));
        await session.DiscoverXFlashStorageAsync(kind, TestContext.Current.CancellationToken);
        return session;
    }

    private sealed class NonSeekableStorageSource(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}