using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Storage;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    private static MtkFlashAreaMap OrdinaryXFlashMap() => MtkFlashAreaMap.FromXFlash(
        MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc, StorageGeometry(MtkXFlashStorageKind.Emmc),
            PacketLengths()));

    [Fact]
    public void FromXFlash_MixedCapacities_SpacesOrdinaryRegionsAndSkipsRpmbAndEmpty()
    {
        var geometry = StorageGeometry(MtkXFlashStorageKind.Emmc);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8 + 4 * 8), 0);
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc, geometry, PacketLengths());

        var map = MtkFlashAreaMap.FromXFlash(storage);

        Assert.Equal(MtkStorageDiscoverySource.XFlash, map.Source);
        Assert.Equal(512, map.AlignmentBytes);
        Assert.Equal(8192, map.ChunkBytes);
        Assert.Equal(
            [
                "XFLASH-EMMC-PARTITION-1", "XFLASH-EMMC-PARTITION-2", "XFLASH-EMMC-PARTITION-4",
                "XFLASH-EMMC-PARTITION-6", "XFLASH-EMMC-PARTITION-7", "EMMC-USER"
            ], map.Regions.Select(region => region.RegionName).ToArray());
        Assert.Equal([1u, 2u, 4u, 6u, 7u, 8u],
            map.Regions.Select(region => region.PhysicalIdentifier).ToArray());
        Assert.Equal([0L, 512L, 1536L, 3584L, 6656L, 10240L],
            map.Regions.Select(region => region.AbsoluteOffset).ToArray());
        Assert.Equal(14336L, map.TotalBytes);
        Assert.DoesNotContain(map.Regions, region => region.RegionName.Contains("RPMB", StringComparison.Ordinal));
    }

    [Fact]
    public void FromXFlash_NandPageGeometry_IsRefusedBecauseItIsNotAFlatArea()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Nand, NandGeometry(), PacketLengths());

        var error = Assert.Throws<NotSupportedException>(() => MtkFlashAreaMap.FromXFlash(storage));

        Assert.Contains("NAND", error.Message, StringComparison.Ordinal);
        Assert.Contains("page", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_RangeCrossingOneBoundary_SplitsOnlyAtTheReportedRegionEdge()
    {
        var map = OrdinaryXFlashMap();

        var segments = map.Plan(1024, 1024);

        Assert.Equal(["XFLASH-EMMC-PARTITION-2", "XFLASH-EMMC-PARTITION-4"],
            segments.Select(segment => segment.RegionName).ToArray());
        Assert.Equal([2u, 4u], segments.Select(segment => segment.PhysicalIdentifier).ToArray());
        Assert.Equal([1024L, 1536L], segments.Select(segment => segment.AbsoluteOffset).ToArray());
        Assert.Equal([512L, 0L], segments.Select(segment => segment.RegionOffset).ToArray());
        Assert.Equal([512L, 512L], segments.Select(segment => segment.Length).ToArray());
    }

    [Theory]
    [InlineData(1, 512)]
    [InlineData(0, 0)]
    [InlineData(0, -512)]
    [InlineData(0, 511)]
    [InlineData(16896, 512)]
    [InlineData(16384, 1024)]
    public void Plan_UnalignedEmptyOrOutsideSpace_FailsClosedBeforeAnyTransfer(long offset, long length)
    {
        var map = OrdinaryXFlashMap();

        Assert.Throws<ArgumentOutOfRangeException>(() => map.Plan(offset, length));
    }

    [Fact]
    public void Plan_EndThatWrapsTheSignedRange_OverflowsInsteadOfAddressingZero()
    {
        var map = OrdinaryXFlashMap();

        Assert.Throws<OverflowException>(() => map.Plan(long.MaxValue & ~511L, 512));
    }

    [Fact]
    public void ResolveSectorRange_MultipliesByTheReportedTransferUnit()
    {
        var map = OrdinaryXFlashMap();

        Assert.Equal((1024L, 2560L), map.ResolveSectorRange(2, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ResolveSectorRange(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ResolveSectorRange(0, 0));
        Assert.Throws<OverflowException>(() => map.ResolveSectorRange(0, long.MaxValue));
    }

    [Fact]
    public async Task ReadXFlashAreaAsync_SpanningThreeRegions_SendsExactPartitionOffsetAndLengthFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var map = MtkFlashAreaMap.FromXFlash(session.XFlashStorage!);
        var payload = Enumerable.Range(0, 3072).Select(index => (byte)index).ToArray();
        EnqueueStorageRead(transport, payload[..512]);
        EnqueueStorageRead(transport, payload[512..1024]);
        EnqueueStorageRead(transport, payload[1024..1536]);
        EnqueueStorageRead(transport, payload[1536..2048]);
        EnqueueStorageRead(transport, payload[2048..2560]);
        EnqueueStorageRead(transport, payload[2560..]);
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.ReadXFlashAreaAsync(map, 0, 3072, destination, progress,
            TestContext.Current.CancellationToken);

        var requests = transport.Writes.Skip(before).Where(bytes => bytes.Length == 56).ToArray();
        Assert.Equal(6, requests.Length);
        Assert.Equal([1u, 2u, 2u, 4u, 4u, 4u],
            requests.Select(bytes => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4))).ToArray());
        Assert.Equal([0L, 0L, 512L, 0L, 512L, 1024L],
            requests.Select(bytes => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))).ToArray());
        Assert.All(requests, bytes => Assert.Equal(512UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16))));
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal([512L, 1024L, 1536L, 2048L, 2560L, 3072L], progress.Values);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashAreaAsync_SegmentAbovePacketBound_ChunksInsideOneRegion()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var map = MtkFlashAreaMap.FromXFlash(session.XFlashStorage!);
        EnqueueStorageRead(transport, new byte[8192]);
        EnqueueStorageRead(transport, new byte[8192]);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await session.ReadXFlashAreaAsync(map, 12800, 16384, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        var requests = transport.Writes.Skip(before).Where(bytes => bytes.Length == 56).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, bytes => Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4))));
        Assert.Equal([0UL, 8192UL],
            requests.Select(bytes => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))).ToArray());
        Assert.All(requests, bytes => Assert.Equal(8192UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16))));
        Assert.Equal(16384, destination.Length);
    }

    [Fact]
    public async Task ReadXFlashAreaAsync_CancelledAfterFirstRegion_StopsWithoutFurtherFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var map = MtkFlashAreaMap.FromXFlash(session.XFlashStorage!);
        EnqueueStorageRead(transport, new byte[512]);
        using var destination = new MemoryStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new InlineStorageProgress(value =>
        {
            if (value >= 512)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashAreaAsync(map, 0, 3072,
            destination, progress, cancellation.Token));

        Assert.Equal(512, destination.Length);
        Assert.Single(transport.Writes, bytes => bytes.Length == 56);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashAreaAsync_OtherFamilyMap_IsRefusedBeforeAnyFrame()
    {
        using var xmlTransport = new ScriptedCommTransport();
        using var xmlSession = await CreateXmlStorageSessionAsync(xmlTransport);
        var xmlMap = MtkFlashAreaMap.FromXml(xmlSession.XmlStorage!);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXFlashAreaAsync(xmlMap, 0, 512,
            destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("XFlash", error.Message, StringComparison.Ordinal);
        Assert.Contains("Xml", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashAreaAsync_MapFromAnotherObservation_IsRefusedBeforeAnyFrame()
    {
        using var otherTransport = new ScriptedCommTransport();
        using var otherSession = await CreateStorageSessionAsync(otherTransport);
        var staleMap = MtkFlashAreaMap.FromXFlash(otherSession.XFlashStorage!);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashAreaAsync(staleMap, 0, 512,
            destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ReadXFlashAreaAsync_PreCancellationOrOutsideSpace_SendsNoFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var map = MtkFlashAreaMap.FromXFlash(session.XFlashStorage!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashAreaAsync(map, 0, 512,
            destination, cancellationToken: cancellation.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashAreaAsync(map, 0, 1L << 40,
            destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadLegacyAreaAsync_CrossesRegionBoundary_SwitchesRegionAndAddressesEachSlice()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var map = MtkFlashAreaMap.FromLegacy(session.LegacyStorage!);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueLegacyRead(transport, disk, 65024, 512);
        EnqueueLegacyRead(transport, disk, 0, 512);
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.ReadLegacyAreaAsync(map, 65024, 1024, destination, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7, map.Regions.Count);
        Assert.Equal(458752L, map.TotalBytes);
        Assert.DoesNotContain(map.Regions, region => region.RegionName.Contains("RPMB", StringComparison.Ordinal));
        var headers = transport.Writes.Skip(before).Where(bytes => bytes.Length == 23 && bytes[0] == 0xD6).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.Equal([65024L, 0L],
            headers.Select(bytes => (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(3))).ToArray());
        Assert.Equal([512L, 512L],
            headers.Select(bytes => (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(11))).ToArray());
        Assert.Equal([1, 2], transport.Writes.Skip(before)
            .Where(bytes => bytes.Length == 1 && bytes[0] is > 0 and <= 8).Select(bytes => bytes[0]).ToArray());
        Assert.Equal([.. disk[65024..65536], .. disk[..512]], destination.ToArray());
        Assert.Equal([512L, 1024L], progress.Values);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXmlAreaAsync_CrossesRegionBoundary_ReadsEachNamedRegionAtItsOwnOffset()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var map = MtkFlashAreaMap.FromXml(session.XmlStorage!);
        Assert.Equal(["EMMC-BOOT1", "EMMC-BOOT2", "EMMC-USER"],
            map.Regions.Select(region => region.RegionName).ToArray());
        Assert.Equal(114688L, map.TotalBytes);
        var offset = map.Regions[0].CapacityBytes - 512;
        EnqueueXmlRead(transport, new byte[512]);
        EnqueueXmlRead(transport, new byte[512]);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await session.ReadXmlAreaAsync(map, offset, 1024, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        var commands = DecodeXmlWriteFrames(transport.Writes.Skip(before))
            .Where(frame => Encoding.UTF8.GetString(frame).Contains("CMD:READ-FLASH", StringComparison.Ordinal))
            .Select(frame => XDocument.Parse(Encoding.UTF8.GetString(frame).TrimEnd('\0')).Root!)
            .ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(["EMMC-BOOT1", "EMMC-BOOT2"],
            commands.Select(command => command.Element("arg")!.Element("partition")!.Value).ToArray());
        Assert.Equal([$"0x{offset:x}", "0x0"],
            commands.Select(command => command.Element("arg")!.Element("offset")!.Value).ToArray());
        Assert.All(commands, command => Assert.Equal("0x200", command.Element("arg")!.Element("length")!.Value));
        Assert.Equal(1024, destination.Length);
        Assert.DoesNotContain(map.Regions, region => region.RegionName.Contains("RPMB", StringComparison.Ordinal));
    }
}