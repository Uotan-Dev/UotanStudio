using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXFlashStorageKind.Emmc, 8u)]
    [InlineData(MtkXFlashStorageKind.Ufs, 3u)]
    public async Task ReadXFlashGptAsync_ValidPair_ReturnsPhysicalSnapshotAndExactReadAddresses(MtkXFlashStorageKind kind, uint region)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport, kind);
        EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var before = transport.Writes.Count;

        var snapshot = await session.ReadXFlashGptAsync(region, TestContext.Current.CancellationToken);

        Assert.Same(session.XFlashStorage, snapshot.Storage);
        Assert.Equal(region, snapshot.PartitionId);
        Assert.Equal("boot", Assert.Single(snapshot.Table.Partitions).Name);
        var requests = transport.Writes.Skip(before).Where(bytes => bytes.Length == 56).ToArray();
        Assert.Equal(4, requests.Length);
        Assert.Equal(new ulong[] { 512, 127 * 512, 1024, 95 * 512 },
            requests.Select(bytes => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))));
        Assert.Equal(new ulong[] { 512, 512, 16384, 16384 },
            requests.Select(bytes => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16))));
        Assert.All(requests, bytes => Assert.Equal(region, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4))));
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashGptAsync_DamagedMetadata_KeepsCompletedProtocolSynchronized()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueStorageRead(transport, new byte[512]);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken));
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        EnqueueStorageRead(transport, new byte[512]);
        using var output = new MemoryStream();
        await session.ReadXFlashStorageAsync(8, 0, 512, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(512, output.Length);
    }

    [Fact]
    public async Task ReadXFlashGptAsync_ReadCompletionRejected_FaultsSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueStorageRead(transport, GptReaderTests.CreateDisk(512)[512..1024], 0xC0070004);
        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken));
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
    }

    [Fact]
    public async Task ReadXFlashGptAsync_InvalidRegionOrPreCancellation_DoesNotSendCommands()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXFlashGptAsync(3, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashGptAsync(9, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashGptAsync(8, cancellation.Token));
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashGptAsync_QueuedRead_CannotInterleaveBetweenMetadataRanges()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        EnqueueStorageRead(transport, new byte[512]);
        using var output = new MemoryStream();
        Task? queued = null;
        var before = transport.Writes.Count;
        transport.OnWrite = bytes =>
        {
            if (bytes.Length == 56 && queued is null)
            {
                queued = session.ReadXFlashStorageAsync(8, 0, 512, output, cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
            }
            return [];
        };

        await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await queued;
        var requests = transport.Writes.Skip(before).Where(bytes => bytes.Length == 56).ToArray();
        Assert.Equal(new ulong[] { 512, 127 * 512, 1024, 95 * 512, 0 },
            requests.Select(bytes => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))));
        Assert.Equal(512, output.Length);
    }

    [Fact]
    public async Task ReadXFlashGptAsync_InFlightCancellation_FaultsAndDoesNotReadBackup()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        EnqueueStorageRead(transport, GptReaderTests.CreateDisk(512)[512..1024]);
        var before = transport.Writes.Count;
        transport.OnWrite = bytes =>
        {
            if (bytes.Length == 56)
            {
                cancellation.Cancel();
            }
            return [];
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashGptAsync(8, cancellation.Token));

        Assert.Single(transport.Writes.Skip(before), bytes => bytes.Length == 56);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
    }

    internal static async Task<MtkSession> CreateGptSessionAsync(ScriptedCommTransport transport,
        MtkXFlashStorageKind kind = MtkXFlashStorageKind.Emmc)
    {
        var session = await CreateStageTwoSessionAsync(transport);
        var geometry = StorageGeometry(kind);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(kind == MtkXFlashStorageKind.Emmc ? 64 : 24), 128 * 512);
        EnqueueControlReply(transport, geometry);
        EnqueueControlReply(transport, PacketLengths());
        await session.DiscoverXFlashStorageAsync(kind, TestContext.Current.CancellationToken);
        return session;
    }

    internal static void EnqueueGpt(ScriptedCommTransport transport, byte[] disk)
    {
        EnqueueStorageRead(transport, disk[512..1024]);
        EnqueueStorageRead(transport, disk[(127 * 512)..]);
        EnqueueStorageRead(transport, disk[1024..(1024 + 16384)]);
        EnqueueStorageRead(transport, disk[(95 * 512)..(127 * 512)]);
    }
}