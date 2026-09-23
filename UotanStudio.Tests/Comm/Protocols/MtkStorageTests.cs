using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXFlashStorageKind.Emmc)]
    [InlineData(MtkXFlashStorageKind.Ufs)]
    public async Task DiscoverXFlashStorageAsync_ValidGeometry_PublishesOnlyAfterPacketQuery(MtkXFlashStorageKind kind)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, StorageGeometry(kind));
        EnqueueControlReply(transport, PacketLengths());

        var storage = await session.DiscoverXFlashStorageAsync(kind, TestContext.Current.CancellationToken);

        Assert.NotNull(storage);
        Assert.Same(storage, session.XFlashStorage);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.Equal(512, storage.BlockSize);
        Assert.Equal(kind, storage.Kind);
        Assert.Equal(4096, storage.WritePacketLength);
        Assert.Equal(8192, storage.ReadPacketLength);
        if (kind == MtkXFlashStorageKind.Emmc)
        {
            Assert.Equal(8, storage.Regions.Count);
            Assert.Equal(4096, storage.Regions[7].CapacityBytes);
            Assert.True(storage.Regions[2].RequiresAuthenticatedAccess);
        }
        else
        {
            Assert.Equal(new long[] { 1024, 512, 1536 }, storage.Regions.Select(region => region.CapacityBytes));
            Assert.Equal(new uint[] { 1, 2, 3 }, storage.Regions.Select(region => region.PartitionId));
        }
    }

    [Fact]
    public async Task DiscoverXFlashStorageAsync_AbsentFamily_AllowsExplicitOtherFamilyQuery()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, new byte[4]);
        Assert.Null(await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Emmc, TestContext.Current.CancellationToken));
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
        Assert.Null(session.XFlashStorage);
        EnqueueControlReply(transport, StorageGeometry(MtkXFlashStorageKind.Ufs));
        EnqueueControlReply(transport, PacketLengths());

        Assert.NotNull(await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Ufs, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoverXFlashStorageAsync_InvalidPacketSizes_DoesNotPublishGeometry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, StorageGeometry(MtkXFlashStorageKind.Emmc));
        EnqueueControlReply(transport, new byte[8]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Emmc, TestContext.Current.CancellationToken));

        Assert.Null(session.XFlashStorage);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("oversized")]
    [InlineData("zero-block")]
    [InlineData("non-power-block")]
    [InlineData("capacity-overflow")]
    [InlineData("capacity-unaligned")]
    [InlineData("empty-storage")]
    [InlineData("short-packets")]
    [InlineData("huge-packets")]
    [InlineData("unaligned-packets")]
    public void ParseStorage_InvalidReports_RejectsWithoutDefaults(string corruption)
    {
        var geometry = StorageGeometry(MtkXFlashStorageKind.Emmc);
        var packets = PacketLengths();
        switch (corruption)
        {
            case "short": geometry = geometry[..80]; break;
            case "oversized": geometry = new byte[4097]; break;
            case "zero-block": BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 0); break;
            case "non-power-block": BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 768); break;
            case "capacity-overflow": BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8), ulong.MaxValue); break;
            case "capacity-unaligned": BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8), 513); break;
            case "empty-storage": geometry.AsSpan(8, 64).Clear(); break;
            case "short-packets": packets = packets[..4]; break;
            case "huge-packets": BinaryPrimitives.WriteUInt32LittleEndian(packets, uint.MaxValue); break;
            case "unaligned-packets": BinaryPrimitives.WriteUInt32LittleEndian(packets, 513); break;
        }

        Assert.Throws<MtkProtocolException>(() => MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc, geometry, packets));
    }

    [Fact]
    public void ValidateRange_CapacityAlignmentAndRpmb_EnforcesOrdinaryStorageBoundary()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());

        storage.ValidateRange(8, 3584, 512);
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(8, 4096, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(8, 1, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(8, 0, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(9, 0, 512));
        Assert.Throws<NotSupportedException>(() => storage.ValidateRange(3, 0, 512));
    }

    private static byte[] StorageGeometry(MtkXFlashStorageKind kind)
    {
        var geometry = new byte[kind == MtkXFlashStorageKind.Emmc ? 96 : 48];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 512);
        for (var index = 0; index < (kind == MtkXFlashStorageKind.Emmc ? 8 : 3); index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8 + index * 8), (ulong)(index + 1) * 512);
        }
        return geometry;
    }

    private static byte[] PacketLengths() => Convert.FromHexString("0010000000200000");

    private static void EnqueueControlReply(ScriptedCommTransport transport, byte[] payload)
    {
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, payload);
        EnqueueXFlashFrame(transport, new byte[4]);
    }

    private static async Task<MtkSession> CreateStageTwoSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, "preloader"u8.ToArray());
        await session.GetXFlashConnectionAgentAsync(TestContext.Current.CancellationToken);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        using var image = new MemoryStream(new byte[8]);
        await session.UploadXFlashStageTwoAsync(image, 0x40000000, 8, cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }
}