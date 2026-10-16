using System.Buffers.Binary;
using System.IO.Hashing;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public partial class FirehoseGptTests
{
    [Fact]
    public async Task ReadGptWithPartitionEntryOverridesAsync_DamagedPrimaryHeaderLocation_ReadsOverrideAndBackupLocations()
    {
        using var transport = Transport(DamagedPrimaryEntryArrayDisk(), 512);
        using var protocol = Protocol(transport, 512);
        protocol.Configuration.Slot = 2;

        var snapshot = await protocol.ReadGptWithPartitionEntryOverridesAsync(5, 60UL, null, null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("boot", Assert.Single(snapshot.Table.Partitions).Name);
        Assert.Equal(5, snapshot.Geometry.PhysicalPartitionNumber);
        Assert.Equal(protocol.Configuration.Slot, snapshot.Slot);
        Assert.True(ReferenceEquals(protocol, snapshot.Owner));
        Assert.Equal(60UL, snapshot.PartitionEntryLba);
        Assert.Equal(60UL, snapshot.Table.PrimaryEntryArrayLba);
        Assert.Equal(95UL, snapshot.Table.BackupEntryArrayLba);
        Assert.False(protocol.RequiresReconnect);
        Assert.Equal(new long[] { 1, 127, 60, 95 }, GptReadStartSectors(transport));
        var reads = Commands(transport).Where(command => command.Name == "read").ToArray();
        Assert.Equal(new long[] { 1, 1, 32, 32 },
            reads.Select(command => (long)command.Attribute("num_partition_sectors")!));
        Assert.All(reads, command => Assert.Equal(5, (int)command.Attribute("physical_partition_number")!));
    }

    [Fact]
    public async Task ReadGptAsync_DamagedPrimaryHeaderLocation_RejectsOverrideFreeReadWithoutFaultingSession()
    {
        using var transport = Transport(DamagedPrimaryEntryArrayDisk(), 512);
        using var protocol = Protocol(transport, 512);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            protocol.ReadGptAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal("The GPT entry array overlaps usable storage or a header.", error.Message);
        Assert.Equal(new long[] { 1 }, GptReadStartSectors(transport));
        Assert.False(protocol.RequiresReconnect);
        Assert.True((await protocol.NopAsync(TestContext.Current.CancellationToken)).IsAck);
    }

    [Theory]
    [InlineData(200UL)]
    [InlineData(1UL)]
    public async Task ReadGptWithPartitionEntryOverridesAsync_EntryArrayBeyondCapacity_StopsAfterThePrimaryHeader(ulong partitionEntryLba)
    {
        using var transport = Transport(GptReaderTests.CreateDisk(512), 512);
        using var protocol = Protocol(transport, 512);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.ReadGptWithPartitionEntryOverridesAsync(0, partitionEntryLba, null, null,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("partitionEntryLba", error.ParamName);
        Assert.Equal(new[] { "getstorageinfo", "read" }, Commands(transport).Select(command => command.Name.LocalName));
        Assert.Equal(new long[] { 1 }, GptReadStartSectors(transport));
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadGptWithPartitionEntryOverridesAsync_WrongLocationInsideCapacity_RejectsBeforeTrustingTable()
    {
        using var transport = Transport(GptReaderTests.CreateDisk(512), 512);
        using var protocol = Protocol(transport, 512);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            protocol.ReadGptWithPartitionEntryOverridesAsync(0, 64UL, null, null,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new long[] { 1, 127, 64, 95 }, GptReadStartSectors(transport));
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadGptWithPartitionEntryOverridesAsync_NullOverride_ProducesSameObservationAsPlainRead()
    {
        using var nullTransport = Transport(GptReaderTests.CreateDisk(512), 512);
        using var nullProtocol = Protocol(nullTransport, 512);
        using var plainTransport = Transport(GptReaderTests.CreateDisk(512), 512);
        using var plainProtocol = Protocol(plainTransport, 512);

        var withNull = await nullProtocol.ReadGptWithPartitionEntryOverridesAsync(0, null, null, null,
            cancellationToken: TestContext.Current.CancellationToken);
        var plain = await plainProtocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        Assert.Null(withNull.PartitionEntryLba);
        Assert.Null(plain.PartitionEntryLba);
        Assert.Equal("boot", Assert.Single(withNull.Table.Partitions).Name);
        Assert.Equal("boot", Assert.Single(plain.Table.Partitions).Name);
        Assert.Equal(2UL, withNull.Table.PrimaryEntryArrayLba);
        Assert.Equal(2UL, plain.Table.PrimaryEntryArrayLba);
        Assert.Equal(plain.Table.MetadataSha256, withNull.Table.MetadataSha256);
        Assert.Equal(new long[] { 1, 127, 2, 95 }, GptReadStartSectors(nullTransport));
        Assert.Equal(GptReadStartSectors(nullTransport), GptReadStartSectors(plainTransport));
    }

    [Fact]
    public async Task ReadGptWithPartitionEntryOverridesAsync_DamagedPrimaryEntryCount_ReadsFullArrayAtOverrideCount()
    {
        using var plainTransport = Transport(DamagedPrimaryEntryCountDisk(), 512);
        using var plainProtocol = Protocol(plainTransport, 512);
        using var transport = Transport(DamagedPrimaryEntryCountDisk(), 512);
        using var protocol = Protocol(transport, 512);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            plainProtocol.ReadGptAsync(0, TestContext.Current.CancellationToken));

        var snapshot = await protocol.ReadGptWithPartitionEntryOverridesAsync(0, null, 128u, null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(128u, snapshot.PartitionEntryCount);
        Assert.Null(snapshot.PartitionEntryLba);
        Assert.Null(snapshot.PartitionEntrySize);
        Assert.Equal("boot", Assert.Single(snapshot.Table.Partitions).Name);
        Assert.Equal(new long[] { 1, 127, 2, 95 }, GptReadStartSectors(transport));
        var reads = Commands(transport).Where(command => command.Name == "read").ToArray();
        Assert.Equal(new long[] { 1, 1, 32, 32 },
            reads.Select(command => (long)command.Attribute("num_partition_sectors")!));
    }

    [Fact]
    public async Task ReadPartitionToFileAsync_OverriddenObservation_RevalidatesAtRecordedLocation()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "boot.bin");
        var disk = DamagedPrimaryEntryArrayDisk();
        var expected = Enumerable.Range(0, 2048).Select(value => (byte)value).ToArray();
        expected.CopyTo(disk, 40 * 512);
        using var transport = Transport(disk, 512);
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptWithPartitionEntryOverridesAsync(2, 60UL, null, null,
            cancellationToken: TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await protocol.ReadPartitionToFileAsync(snapshot, "boot", path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "getstorageinfo", "read", "read", "read", "read", "read" },
            Commands(transport).Skip(before).Select(command => command.Name.LocalName));
        Assert.Equal(new long[] { 1, 127, 60, 95, 40 }, GptReadStartSectors(transport, before));
        var reads = Commands(transport).Skip(before).Where(command => command.Name == "read").ToArray();
        Assert.All(reads, command => Assert.Equal(2, (int)command.Attribute("physical_partition_number")!));
        var gate = transport.Writes.Count;

        RelabelBootPartition(disk);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => protocol.ReadPartitionToFileAsync(snapshot,
            "boot", path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("The GPT observation changed; no named partition operation was started.", error.Message);
        Assert.Equal(new long[] { 1, 127, 60, 95 }, GptReadStartSectors(transport, gate));
        Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadPartitionToFileAsync_AllThreeOverridesRecorded_RevalidatesWithSameDimensions()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "boot.bin");
        var disk = DamagedPrimaryEntryArrayDisk();
        var expected = Enumerable.Range(0, 2048).Select(value => (byte)value).ToArray();
        expected.CopyTo(disk, 40 * 512);
        using var transport = Transport(disk, 512);
        using var protocol = Protocol(transport, 512);

        var snapshot = await protocol.ReadGptWithPartitionEntryOverridesAsync(2, 60UL, 128u, 128u,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(60UL, snapshot.PartitionEntryLba);
        Assert.Equal(128u, snapshot.PartitionEntryCount);
        Assert.Equal(128u, snapshot.PartitionEntrySize);
        Assert.Equal(new long[] { 1, 127, 60, 95 }, GptReadStartSectors(transport));
        var arrayReads = Commands(transport).Where(command => command.Name == "read")
            .Where(command => (long)command.Attribute("start_sector")! is 60 or 95).ToArray();
        Assert.Equal(2, arrayReads.Length);
        Assert.All(arrayReads, command => Assert.Equal(32, (int)command.Attribute("num_partition_sectors")!));
        var before = transport.Writes.Count;

        await protocol.ReadPartitionToFileAsync(snapshot, "boot", path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(new long[] { 1, 127, 60, 95, 40 }, GptReadStartSectors(transport, before));
    }

    [Fact]
    public async Task ReadPartitionToFileAsync_OverriddenSnapshotFromAnotherProtocol_RejectsByOwnerNotOverride()
    {
        using var dir = new TempDirectory();
        var disk = GptReaderTests.CreateDisk(512);
        using var ownerTransport = Transport(disk, 512);
        using var owner = Protocol(ownerTransport, 512);
        using var otherTransport = Transport(disk, 512);
        using var other = Protocol(otherTransport, 512);
        var table = new GptTable(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 512, 128,
            [new GptPartition(0, Guid.Parse("0fc63daf-8483-4772-8e79-3d69d8477de4"),
                Guid.Parse("11111111-2222-3333-4444-555555555555"), 40, 43, 0, "boot", 20480, 2048)],
            "0000000000000000000000000000000000000000000000000000000000000000", 92, 34, 94, 128, 128, 60, 95);
        var snapshot = new FirehoseGptSnapshot(owner, new FirehoseStorageGeometry(0, 512, 128), 0, table, 60UL);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => other.ReadPartitionToFileAsync(snapshot,
            "boot", Path.Combine(dir.Path, "other.bin"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(60UL, snapshot.PartitionEntryLba);
        Assert.Equal("The GPT snapshot belongs to a different protocol or storage configuration.", error.Message);
        Assert.Empty(otherTransport.Writes);
        Assert.Empty(Directory.GetFiles(dir.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() => owner.ReadPartitionToFileAsync(snapshot, "boot",
            Path.Combine(dir.Path, "owner.bin"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new long[] { 1, 127, 60, 95 }, GptReadStartSectors(ownerTransport));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    private static byte[] DamagedPrimaryEntryArrayDisk()
    {
        var disk = GptReaderTests.CreateDisk(512);
        disk.AsSpan(1024, 16384).CopyTo(disk.AsSpan(60 * 512));
        var header = disk.AsSpan(512, 512);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 3);
        header[16..20].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.HashToUInt32(header[..92]));
        return disk;
    }

    private static byte[] DamagedPrimaryEntryCountDisk()
    {
        var disk = GptReaderTests.CreateDisk(512);
        var header = disk.AsSpan(512, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 0u);
        header[16..20].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.HashToUInt32(header[..92]));
        return disk;
    }

    private static void RelabelBootPartition(byte[] disk)
    {
        var primary = disk.AsSpan(60 * 512, 16384);
        var backup = disk.AsSpan(95 * 512, 16384);
        primary[56] = 0x6C;
        backup[56] = 0x6C;
        foreach (var lba in new[] { 1, 127 })
        {
            var header = disk.AsSpan(lba * 512, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(header[88..], Crc32.HashToUInt32(primary));
            header[16..20].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.HashToUInt32(header[..92]));
        }
    }

    private static long[] GptReadStartSectors(ScriptedCommTransport transport, int commandOffset = 0) =>
        Commands(transport).Skip(commandOffset).Where(command => command.Name == "read")
            .Select(command => (long)command.Attribute("start_sector")!).ToArray();
}