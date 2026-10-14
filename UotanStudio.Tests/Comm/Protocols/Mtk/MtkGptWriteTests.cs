using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    private const long PrimaryGptExtentBytes = 34 * 512;
    private const long BackupGptExtentBytes = 33 * 512;
    private const long BackupGptExtentOffset = 95 * 512;

    private static void EnqueueXFlashWrite(ScriptedCommTransport transport, long length, int packetBytes)
    {
        EnqueueTransferStart(transport, read: false);
        var packets = (length + packetBytes - 1) / packetBytes;
        for (var index = 0; index < packets; index++)
        {
            EnqueueXFlashFrame(transport, new byte[4]);
        }

        EnqueueXFlashValue(transport, 0);
    }

    private static void EnqueueLegacyWrite(ScriptedCommTransport transport, long length, int packetBytes)
    {
        transport.Enqueue([0x5A]);
        var packets = (length + packetBytes - 1) / packetBytes;
        for (var index = 0; index < packets; index++)
        {
            transport.Enqueue([0x69]);
        }
    }

    private static List<byte[]> WriteRequests(ScriptedCommTransport transport, int from)
    {
        var requests = new List<byte[]>();
        for (var index = from; index < transport.Writes.Count; index++)
        {
            if (transport.Writes[index].Length == 56 && index >= 2
                && transport.Writes[index - 2].SequenceEqual(Convert.FromHexString("04000100")))
            {
                requests.Add(transport.Writes[index]);
            }
        }

        return requests;
    }

    [Fact]
    public void Plan_XmlSnapshot_SerializesOwnCopiesAndLeavesTheUsableAreaUntouched()
    {
        var table = GptReader.Read(new MemoryStream(GptReaderTests.CreateDisk(512, 128, 128)), 512, 128,
            TestContext.Current.CancellationToken);
        var storage = MtkXmlStorageInfo.Parse(
            "<da_hw_info><version>1.2</version><storage>EMMC</storage><emmc><block_size>0x200</block_size>" +
            "<boot1_size>0x0</boot1_size><boot2_size>0x0</boot2_size><rpmb_size>0x2000</rpmb_size>" +
            "<user_size>0x10000</user_size><gp1_size>0x0</gp1_size><gp2_size>0x0</gp2_size>" +
            "<gp3_size>0x0</gp3_size><gp4_size>0x0</gp4_size></emmc></da_hw_info>");
        var snapshot = new MtkXmlGptSnapshot(storage, "EMMC-USER", table);

        var extents = MtkGptWritePlanner.Plan(snapshot);

        Assert.Equal(2, extents.Count);
        Assert.Equal([MtkGptCopy.Primary, MtkGptCopy.Backup], extents.Select(extent => extent.Copy).ToArray());
        Assert.Equal([0L, BackupGptExtentOffset], extents.Select(extent => extent.Offset).ToArray());
        Assert.Equal([PrimaryGptExtentBytes, BackupGptExtentBytes], extents.Select(extent => extent.Length).ToArray());
        Assert.All(extents, extent => Assert.Equal("EMMC-USER", extent.RegionName));
        Assert.All(extents, extent => Assert.Null(extent.PhysicalIdentifier));
        Assert.Equal(PrimaryGptExtentBytes, extents[0].Content.LongLength);
        Assert.True(extents[0].Offset + extents[0].Length <= extents[1].Offset);
        Assert.Equal("boot", Assert.Single(table.Partitions).Name);
        Assert.True(extents[0].Offset + extents[0].Length <= table.Partitions[0].ByteOffset);
        Assert.True(extents[1].Offset + extents[1].Length <= 65536);
    }

    [Fact]
    public void Plan_XmlSnapshotOnAWiderRegion_ProgramsTheBackupAtTheArrayTheTableDeclaresNotAtTheRegionEnd()
    {
        var table = GptReader.Read(new MemoryStream(GptReaderTests.CreateDisk(512, 128, 128)), 512, 128,
            TestContext.Current.CancellationToken);
        var storage = MtkXmlStorageInfo.Parse(
            "<da_hw_info><version>1.2</version><storage>EMMC</storage><emmc><block_size>0x200</block_size>" +
            "<boot1_size>0x0</boot1_size><boot2_size>0x0</boot2_size><rpmb_size>0x2000</rpmb_size>" +
            "<user_size>0x20000</user_size><gp1_size>0x0</gp1_size><gp2_size>0x0</gp2_size>" +
            "<gp3_size>0x0</gp3_size><gp4_size>0x0</gp4_size></emmc></da_hw_info>");
        var snapshot = new MtkXmlGptSnapshot(storage, "EMMC-USER", table);

        var extents = MtkGptWritePlanner.Plan(snapshot);

        Assert.Equal(131072L, storage.Regions.Single(region => region.Name == "EMMC-USER").CapacityBytes);
        Assert.Equal(BackupGptExtentOffset, extents[1].Offset);
        Assert.NotEqual(131072L - BackupGptExtentBytes, extents[1].Offset);
        Assert.Equal(BackupGptExtentBytes, extents[1].Length);
        Assert.Equal((ulong)table.BackupEntryArrayLba * 512UL, (ulong)extents[1].Offset);
    }

    [Fact]
    public void Plan_TableClaimingMoreBlocksThanTheRegion_IsRefusedBeforeAnyByte()
    {
        var table = GptReader.Read(new MemoryStream(GptReaderTests.CreateDisk(512, 128, 128)), 512, 128,
            TestContext.Current.CancellationToken);
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());
        var snapshot = new MtkGptSnapshot(storage, 8, table);

        var error = Assert.Throws<InvalidDataException>(() => MtkGptWritePlanner.Plan(snapshot));

        Assert.Contains("128", error.Message, StringComparison.Ordinal);
        Assert.Contains("4096", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_TableReadWithAnotherBlockSize_IsRefusedBeforeAnyByte()
    {
        var table = GptReader.Read(new MemoryStream(GptReaderTests.CreateDisk(4096, 128, 128)), 4096, 128,
            TestContext.Current.CancellationToken);
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());
        var snapshot = new MtkGptSnapshot(storage, 8, table);

        var error = Assert.Throws<InvalidDataException>(() => MtkGptWritePlanner.Plan(snapshot));

        Assert.Contains("blocks", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteXFlashGptAsync_ValidSnapshot_ProgramsBothCopiesThenProvesByteAndStructureEquivalence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var extents = MtkGptWritePlanner.Plan(snapshot);
        EnqueueGpt(transport, disk);
        EnqueueXFlashWrite(transport, PrimaryGptExtentBytes, 4096);
        EnqueueXFlashWrite(transport, BackupGptExtentBytes, 4096);
        EnqueueStorageRead(transport, extents[0].Content);
        EnqueueStorageRead(transport, extents[1].Content);
        EnqueueGpt(transport, disk);
        var before = transport.Writes.Count;

        var result = await session.WriteXFlashGptAsync(snapshot,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExtentsProgrammed);
        Assert.Equal(PrimaryGptExtentBytes + BackupGptExtentBytes, result.BytesProgrammed);
        Assert.Equal(result.BytesProgrammed, result.BytesCompared);
        Assert.True(result.BytesEquivalent);
        Assert.True(result.StructureEquivalent);
        Assert.True(result.CapacityEquivalent);
        Assert.Equal(snapshot.Table.MetadataSha256, result.WrittenMetadataSha256);
        Assert.Equal(result.WrittenMetadataSha256, result.ReadbackMetadataSha256);
        var requests = WriteRequests(transport, before);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, bytes => Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4))));
        Assert.All(requests, bytes => Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0))));
        Assert.All(requests, bytes => Assert.True(bytes[24..].SequenceEqual(new byte[32]),
            "The NAND extension words of the XFlash storage parameter block must stay at their default zeros."));
        Assert.Equal([0L, BackupGptExtentOffset],
            requests.Select(bytes => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))).ToArray());
        Assert.Equal([PrimaryGptExtentBytes, BackupGptExtentBytes],
            requests.Select(bytes => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16))).ToArray());
        var programmed = transport.Writes.Skip(before)
            .Where(bytes => bytes.Length is 4096 or 1024 or 512).SelectMany(bytes => bytes).ToArray();
        Assert.Equal([.. extents[0].Content, .. extents[1].Content], programmed);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashGptAsync_LiveTableDriftedBeforeSubmit_StopsBeforeAnyWriteRequest()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        EnqueueStorageRead(transport, new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => session.WriteXFlashGptAsync(snapshot,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(WriteRequests(transport, before));
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashGptAsync_ReadbackDiffers_ReportsFailureWithoutRetrying()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var extents = MtkGptWritePlanner.Plan(snapshot);
        EnqueueGpt(transport, disk);
        EnqueueXFlashWrite(transport, PrimaryGptExtentBytes, 4096);
        EnqueueXFlashWrite(transport, BackupGptExtentBytes, 4096);
        var damaged = extents[0].Content.ToArray();
        damaged[0] ^= 0xFF;
        EnqueueStorageRead(transport, damaged);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.WriteXFlashGptAsync(snapshot,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("differs", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, WriteRequests(transport, before).Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashGptAsync_ObservationFromAnotherSession_IsRefusedBeforeAnyFrame()
    {
        using var otherTransport = new ScriptedCommTransport();
        using var otherSession = await CreateGptSessionAsync(otherTransport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueGpt(otherTransport, disk);
        var foreign = await otherSession.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteXFlashGptAsync(foreign,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashGptAsync_PreCancellation_SendsNoFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WriteXFlashGptAsync(snapshot,
            cancellationToken: cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task WriteLegacyGptAsync_ValidSnapshot_ProgramsBothCopiesWithExactRegionHeaders()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueLegacyGptReads(transport, disk);
        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        var extents = MtkGptWritePlanner.Plan(snapshot);
        EnqueueLegacyGptReads(transport, disk);
        EnqueueLegacyWrite(transport, PrimaryGptExtentBytes, 512);
        EnqueueLegacyWrite(transport, BackupGptExtentBytes, 512);
        EnqueueLegacyRead(transport, extents[0].Content, 0, PrimaryGptExtentBytes);
        EnqueueLegacyRead(transport, extents[1].Content, 0, BackupGptExtentBytes);
        EnqueueLegacyGptReads(transport, disk);
        var before = transport.Writes.Count;

        var result = await session.WriteLegacyGptAsync(snapshot,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExtentsProgrammed);
        Assert.Equal(PrimaryGptExtentBytes + BackupGptExtentBytes, result.BytesProgrammed);
        Assert.True(result.BytesEquivalent);
        Assert.True(result.StructureEquivalent);
        Assert.True(result.CapacityEquivalent);
        var headers = transport.Writes.Skip(before)
            .Where(bytes => bytes.Length == 23 && bytes[0] == 0x62).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.All(headers, bytes => Assert.Equal(8, bytes[2]));
        Assert.All(headers, bytes => Assert.Equal(1, bytes[1]));
        Assert.All(headers, bytes => Assert.Equal(512u,
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(19))));
        var writesAfter = transport.Writes.Skip(before).ToList();
        Assert.Equal([8], writesAfter
            .Select((bytes, index) => (bytes, index))
            .Where(pair => pair.bytes.Length == 1 && pair.bytes[0] == 0x60
                           && pair.index + 1 < writesAfter.Count)
            .Select(pair => writesAfter[pair.index + 1][0])
            .Distinct()
            .ToArray());
        Assert.Equal([0L, BackupGptExtentOffset],
            headers.Select(bytes => (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(3))).ToArray());
        Assert.Equal([PrimaryGptExtentBytes, BackupGptExtentBytes],
            headers.Select(bytes => (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(11))).ToArray());
        var programmed = transport.Writes.Skip(before).Where(bytes => bytes.Length == 512)
            .SelectMany(bytes => bytes).ToArray();
        Assert.Equal([.. extents[0].Content, .. extents[1].Content], programmed);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXmlGptAsync_ValidSnapshot_ProgramsBothCopiesThroughWriteFlashOnly()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueXmlGptReads(transport, disk);
        var snapshot = await session.ReadXmlGptAsync("EMMC-USER", cancellationToken: TestContext.Current.CancellationToken);
        var extents = MtkGptWritePlanner.Plan(snapshot);
        EnqueueXmlGptReads(transport, disk);
        EnqueueXmlWrite(transport, (int)PrimaryGptExtentBytes, 512);
        EnqueueXmlWrite(transport, (int)BackupGptExtentBytes, 512);
        EnqueueXmlRead(transport, extents[0].Content, 512);
        EnqueueXmlRead(transport, extents[1].Content, 512);
        EnqueueXmlGptReads(transport, disk);
        var before = transport.Writes.Count;

        var result = await session.WriteXmlGptAsync(snapshot,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExtentsProgrammed);
        Assert.Equal(PrimaryGptExtentBytes + BackupGptExtentBytes, result.BytesProgrammed);
        Assert.True(result.BytesEquivalent);
        Assert.True(result.StructureEquivalent);
        Assert.True(result.CapacityEquivalent);
        var commands = DecodeXmlWriteFrames(transport.Writes.Skip(before))
            .Where(frame => Encoding.UTF8.GetString(frame).Contains("CMD:WRITE-FLASH", StringComparison.Ordinal))
            .Select(frame => XDocument.Parse(Encoding.UTF8.GetString(frame).TrimEnd('\0')).Root!)
            .ToArray();
        Assert.Equal(2, commands.Length);
        Assert.All(commands, command => Assert.Equal("EMMC-USER", command.Element("arg")!.Element("partition")!.Value));
        Assert.Equal(["0x0", "0xbe00"],
            commands.Select(command => command.Element("arg")!.Element("offset")!.Value).ToArray());
        Assert.Contains(DecodeXmlWriteFrames(transport.Writes.Skip(before)),
            frame => frame.SequenceEqual(Encoding.UTF8.GetBytes($"OK@0x{PrimaryGptExtentBytes:x}\0")));
    }

    [Fact]
    public async Task WriteXmlGptAsync_AuthenticatedRegionSnapshot_IsImpossibleAndForeignSnapshotRefused()
    {
        using var otherTransport = new ScriptedCommTransport();
        using var otherSession = await CreateXmlStorageSessionAsync(otherTransport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueXmlGptReads(otherTransport, disk);
        var foreign = await otherSession.ReadXmlGptAsync("EMMC-USER",
            cancellationToken: TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteXmlGptAsync(foreign,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task WriteLegacyGptAsync_ObservationFromAnotherSession_IsRefusedBeforeAnyFrame()
    {
        using var otherTransport = new ScriptedCommTransport();
        using var otherSession = await CreateLegacyGptSessionAsync(otherTransport);
        EnqueueLegacyGptReads(otherTransport, GptReaderTests.CreateDisk(512, 128, 128));
        var foreign = await otherSession.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteLegacyGptAsync(foreign,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task WriteLegacyGptAsync_LiveTableDriftedBeforeSubmit_StopsBeforeAnyWriteRequest()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueLegacyGptReads(transport, disk);
        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        EnqueueLegacyRead(transport, new byte[512 * LegacyGptBlockCount], 512, 512);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => session.WriteLegacyGptAsync(snapshot,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain(transport.Writes.Skip(before), bytes => bytes.Length == 23 && bytes[0] == 0x62);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task WriteLegacyGptAsync_ReadbackDiffers_ReportsFailureWithoutRetrying()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueLegacyGptReads(transport, disk);
        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        var extents = MtkGptWritePlanner.Plan(snapshot);
        EnqueueLegacyGptReads(transport, disk);
        EnqueueLegacyWrite(transport, PrimaryGptExtentBytes, 512);
        EnqueueLegacyWrite(transport, BackupGptExtentBytes, 512);
        EnqueueLegacyRead(transport, extents[0].Content, 0, PrimaryGptExtentBytes);
        var damaged = extents[1].Content.ToArray();
        damaged[damaged.Length - 1] ^= 0xFF;
        EnqueueLegacyRead(transport, damaged, 0, BackupGptExtentBytes);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.WriteLegacyGptAsync(snapshot,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("differs", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, transport.Writes.Skip(before).Count(bytes => bytes.Length == 23 && bytes[0] == 0x62));
    }

    [Fact]
    public async Task WriteLegacyGptAsync_PreCancellation_SendsNoFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, GptReaderTests.CreateDisk(512, 128, 128));
        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WriteLegacyGptAsync(snapshot,
            cancellationToken: cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task WriteXmlGptAsync_PreCancellation_SendsNoFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlGptReads(transport, GptReaderTests.CreateDisk(512, 128, 128));
        var snapshot = await session.ReadXmlGptAsync("EMMC-USER",
            cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WriteXmlGptAsync(snapshot,
            cancellationToken: cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task WriteXmlGptAsync_ReadbackDiffers_ReportsFailureWithoutRetrying()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueXmlGptReads(transport, disk);
        var snapshot = await session.ReadXmlGptAsync("EMMC-USER",
            cancellationToken: TestContext.Current.CancellationToken);
        var extents = MtkGptWritePlanner.Plan(snapshot);
        EnqueueXmlGptReads(transport, disk);
        EnqueueXmlWrite(transport, (int)PrimaryGptExtentBytes, 512);
        EnqueueXmlWrite(transport, (int)BackupGptExtentBytes, 512);
        var damaged = extents[0].Content.ToArray();
        damaged[0] ^= 0xFF;
        EnqueueXmlRead(transport, damaged, 512);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.WriteXmlGptAsync(snapshot,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("differs", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, DecodeXmlWriteFrames(transport.Writes.Skip(before))
            .Count(frame => Encoding.UTF8.GetString(frame).Contains("CMD:WRITE-FLASH", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Plan_LegacySnapshotNamingTheRpmbRegion_IsRefusedBeforeAnyByte()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var storage = session.LegacyStorage ?? throw new InvalidOperationException("Legacy storage was not discovered.");
        var table = GptReader.Read(new MemoryStream(GptReaderTests.CreateDisk(512, 128, 128)), 512, 128,
            TestContext.Current.CancellationToken);
        var snapshot = new MtkLegacyGptSnapshot(storage, 3, table);

        var error = Assert.Throws<NotSupportedException>(() => MtkGptWritePlanner.Plan(snapshot));

        Assert.Contains("authenticated", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}