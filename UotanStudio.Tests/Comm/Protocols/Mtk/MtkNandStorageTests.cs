using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(4096)]
    [InlineData(2112)]
    public async Task DiscoverXFlashStorageAsync_NandGeometry_PreservesMainSpareAndPhysicalCapacity(int packetLength)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, NandGeometry());
        EnqueueControlReply(transport, NorPacketLengths(packetLength));
        var before = transport.Writes.Count;

        var storage = await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Nand, TestContext.Current.CancellationToken);

        Assert.NotNull(storage);
        Assert.Equal(0x040002u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[before + 3]));
        Assert.Equal(MtkXFlashStorageKind.Nand, storage.Kind);
        Assert.Equal(2048, storage.BlockSize);
        Assert.Equal(packetLength, storage.WritePacketLength);
        Assert.Equal(packetLength, storage.ReadPacketLength);
        Assert.Empty(storage.CidHex);
        var nand = Assert.IsType<MtkXFlashNandGeometry>(storage.Nand);
        Assert.Equal(2048, nand.PageSize);
        Assert.Equal(131072, nand.EraseBlockSize);
        Assert.Equal(64, nand.SpareSize);
        Assert.Equal(1048576, nand.TotalSize);
        Assert.Equal(786432, nand.AvailableSize);
        Assert.True(nand.HasBadBlockTable);
        Assert.Equal("0102030405060708090A0B0C", nand.DeviceIdHex);
        var region = Assert.Single(storage.Regions);
        Assert.Equal(8u, region.PartitionId);
        Assert.Equal(nand.AvailableSize, region.CapacityBytes);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("zero-page")]
    [InlineData("huge-page")]
    [InlineData("non-power-page")]
    [InlineData("zero-block")]
    [InlineData("unaligned-block")]
    [InlineData("huge-block")]
    [InlineData("huge-spare")]
    [InlineData("zero-total")]
    [InlineData("unaligned-total")]
    [InlineData("overflow-total")]
    [InlineData("unaligned-available")]
    [InlineData("huge-available")]
    [InlineData("invalid-bmt")]
    public async Task DiscoverXFlashStorageAsync_InvalidNandReport_FaultsWithoutPublishing(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var bytes = NandGeometry();
        switch (failure)
        {
            case "short": bytes = bytes[..44]; break;
            case "zero-page": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0); break;
            case "huge-page": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 131072); break;
            case "non-power-page": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1536); break;
            case "zero-block": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0); break;
            case "unaligned-block": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4097); break;
            case "huge-block": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), uint.MaxValue); break;
            case "huge-spare": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 2049); break;
            case "zero-total": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0); break;
            case "unaligned-total": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 1048577); break;
            case "overflow-total": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), ulong.MaxValue); break;
            case "unaligned-available": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 786433); break;
            case "huge-available": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 1048576 * 2); break;
            case "invalid-bmt": bytes[32] = 2; break;
        }
        EnqueueControlReply(transport, bytes);
        EnqueueControlReply(transport, NorPacketLengths(4096));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.DiscoverXFlashStorageAsync(
            MtkXFlashStorageKind.Nand, TestContext.Current.CancellationToken));

        Assert.Null(session.XFlashStorage);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task UnifiedBootstrapAsync_NandProfile_CompletesExplicitNandDiscovery()
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        var options = BootstrapOptions(dir) with { StorageKind = MtkXFlashStorageKind.Nand };
        EnqueueBootstrap(transport, false, geometry: NandGeometry());

        using var session = await MtkBootstrap.InitializeAsync(
            transport, MtkDaProtocol.XFlash, options, TestContext.Current.CancellationToken);

        Assert.Equal(MtkXFlashStorageKind.Nand, session.XFlashStorage!.Kind);
        Assert.NotNull(session.XFlashStorage.Nand);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task UnifiedBootstrapAsync_NandOnXmlProtocol_RejectsBeforeSerialOpen()
    {
        using var dir = new TempDirectory();
        var options = XmlBootstrapOptions(dir) with { StorageKind = MtkXFlashStorageKind.Nand };
        using var transport = new ScriptedCommTransport();
        var opens = 0;

        await Assert.ThrowsAsync<ArgumentException>(() => MtkBootstrap.ConnectSerialCoreAsync(
            ExplicitSerial(), MtkDaProtocol.Xml, options, (_, _) =>
            {
                opens++;
                return Task.FromResult<ICommTransport>(transport);
            }, TestContext.Current.CancellationToken));

        Assert.Equal(0, opens);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task NandStorageAsync_OrdinaryBlockInterfaces_RejectBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        using var stream = new MemoryStream(new byte[2048]);
        var before = transport.Writes.Count;

        Assert.Throws<NotSupportedException>(() => session.XFlashStorage!.ValidateRange(8, 0, 2048));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXFlashStorageAsync(
            8, 0, 2048, stream, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.WriteXFlashStorageAsync(
            8, 0, 2048, stream, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.EraseXFlashStorageAsync(
            8, 0, 131072, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(MtkXFlashNandTransferMode.PageAndSpare, 6336L)]
    [InlineData(MtkXFlashNandTransferMode.PageOnly, 6144L)]
    [InlineData(MtkXFlashNandTransferMode.SpareOnly, 192L)]
    [InlineData(MtkXFlashNandTransferMode.PageWithEcc, 6144L)]
    [InlineData(MtkXFlashNandTransferMode.PageAndSpareWithEcc, 6336L)]
    [InlineData(MtkXFlashNandTransferMode.PageAndSpareNoRandom, 6336L)]
    public void GetTransferLength_NandModes_SeparatesMainSpanFromStream(MtkXFlashNandTransferMode mode, long expected)
    {
        var nand = MtkXFlashNandGeometry.Parse(NandGeometry());

        Assert.Equal(expected, NandOptions(mode).GetTransferLength(nand, 6144));
    }

    [Fact]
    public void GetTransferLength_NandInterleavedOverflow_RejectsBeforeAllocation()
    {
        var bytes = NandGeometry();
        var total = (ulong)(long.MaxValue & ~131071L);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), total);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), total);
        var nand = MtkXFlashNandGeometry.Parse(bytes);

        Assert.Throws<ArgumentOutOfRangeException>(() => NandOptions(MtkXFlashNandTransferMode.PageAndSpare)
            .GetTransferLength(nand, (long)total));
        Assert.Equal((long)total, NandOptions(MtkXFlashNandTransferMode.PageOnly).GetTransferLength(nand, (long)total));
    }

    [Fact]
    public void NandParameters_ExplicitEightWordProfile_MatchesGoldenLittleEndianLayout()
    {
        var geometry = MtkXFlashNandGeometry.Parse(NandGeometry());
        var policy = new MtkXFlashNandPolicy
        {
            AddressType = MtkXFlashNandAddressType.Physical,
            CellUsage = MtkXFlashNandCellUsage.MultiLevel,
            StorageType = MtkXFlashNandStorageType.Spi,
            Region = 7,
            SystemSlcPercent = 11,
            UserSlcPercent = 22,
            PhysicalMaximumSizeKiB = 512
        };

        var parameters = policy.CreateParameters(geometry, 131072, 262144, 8192, 2, false);

        Assert.Equal(Convert.FromHexString(
            "15000000080000000000020000000000002000000000000001000000010000000000000007000000020000000B0000001600000000020000"),
            parameters);
    }

    [Theory]
    [InlineData(MtkXFlashNandTransferMode.PageAndSpare, 0u)]
    [InlineData(MtkXFlashNandTransferMode.PageOnly, 1u)]
    [InlineData(MtkXFlashNandTransferMode.SpareOnly, 2u)]
    [InlineData(MtkXFlashNandTransferMode.PageWithEcc, 3u)]
    [InlineData(MtkXFlashNandTransferMode.PageAndSpareWithEcc, 4u)]
    [InlineData(MtkXFlashNandTransferMode.PageAndSpareNoRandom, 6u)]
    public void NandTransferMode_DefinedOperation_PreservesWireValue(MtkXFlashNandTransferMode mode, uint wireValue)
    {
        Assert.Equal(wireValue, (uint)mode);
    }

    private static byte[] NandGeometry(int spareSize = 64, long available = 786432)
    {
        var bytes = new byte[45];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 131072);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)spareSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 1048576);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), (ulong)available);
        bytes[32] = 1;
        for (var index = 0; index < 12; index++)
        {
            bytes[33 + index] = (byte)(index + 1);
        }
        return bytes;
    }

    private static MtkXFlashNandTransferOptions NandOptions(
        MtkXFlashNandTransferMode mode = MtkXFlashNandTransferMode.PageOnly,
        MtkXFlashNandLengthUnit unit = MtkXFlashNandLengthUnit.MainAreaBytes) => new()
        {
            Mode = mode,
            LengthUnit = unit,
            Policy = new MtkXFlashNandPolicy
            {
                AddressType = MtkXFlashNandAddressType.Logical,
                CellUsage = MtkXFlashNandCellUsage.SingleLevel
            }
        };

    private static async Task<MtkSession> CreateNandSessionAsync(ScriptedCommTransport transport, int packetLength = 4096,
        int spareSize = 64, long available = 786432)
    {
        var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, NandGeometry(spareSize, available));
        EnqueueControlReply(transport, NorPacketLengths(packetLength));
        await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Nand, TestContext.Current.CancellationToken);
        return session;
    }
}