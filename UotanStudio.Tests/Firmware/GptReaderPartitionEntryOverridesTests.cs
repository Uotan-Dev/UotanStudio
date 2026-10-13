using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using UotanStudio.Firmware.BlockImage.Gpt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class GptReaderPartitionEntryOverridesTests
{
    private const int EntrySizeBytes = 128;
    private const int MinimumArrayBytes = 16384;

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_DamagedHeaderArrayLba_RecoversDisplacedArray()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        const int entryCount = 128;
        var disk = GptReaderTests.CreateDisk(blockSize);
        var healthy = await GptReader.ReadAsync(ReadBlocks, blockSize, blockCount,
            cancellationToken: TestContext.Current.CancellationToken);
        disk = CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, 60UL);
        var table = await GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, 60UL, null,
            null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(healthy.Partitions, table.Partitions);
        Assert.Equal(healthy.DiskId, table.DiskId);
        Assert.Equal(healthy.BlockSize, table.BlockSize);
        Assert.Equal(healthy.BlockCount, table.BlockCount);
        Assert.Equal(healthy.FirstUsableLba, table.FirstUsableLba);
        Assert.Equal(healthy.LastUsableLba, table.LastUsableLba);
        Assert.Equal(healthy.PartitionEntryCount, table.PartitionEntryCount);
        Assert.Equal(healthy.PartitionEntrySize, table.PartitionEntrySize);
        Assert.Equal(2UL, healthy.PrimaryEntryArrayLba);
        Assert.Equal(60UL, table.PrimaryEntryArrayLba);
        Assert.Equal(95UL, table.BackupEntryArrayLba);
        Assert.NotEqual(healthy.MetadataSha256, table.MetadataSha256);
        var partition = Assert.Single(table.Partitions);
        Assert.Equal("boot", partition.Name);
        Assert.Equal(new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4"), partition.TypeId);
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), partition.Id);
        Assert.Equal(40UL, partition.FirstLba);
        Assert.Equal(43UL, partition.LastLba);
        Assert.Equal(40L * blockSize, partition.ByteOffset);
        Assert.Equal(4L * blockSize, partition.ByteLength);
        Assert.Same(partition, table.FindPartition("boot"));

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_DisplacedArray_ReadsOverrideAndBackupLocationsOnly()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        const int entryCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var (arrayBytes, _, _, backupArrayLba) = Geometry(blockSize, entryCount, blockCount);
        var disk = CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, 60UL);
        var table = await GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, 60UL, null,
            null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(60UL, table.PrimaryEntryArrayLba);
        Assert.Equal((ulong)backupArrayLba, table.BackupEntryArrayLba);
        Assert.Equal(new[]
        {
            1L * blockSize,
            (long)(blockCount - 1) * blockSize,
            60L * blockSize,
            (long)backupArrayLba * blockSize
        }, reads.Select(read => read.Offset));
        Assert.Equal(new[] { blockSize, blockSize, arrayBytes, arrayBytes }, reads.Select(read => read.Length));
        Assert.All(reads, read =>
        {
            Assert.Equal(0, read.Offset % blockSize);
            Assert.Equal(0, read.Length % blockSize);
            Assert.InRange(read.Length, blockSize, 65536);
        });

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadWithPartitionEntryOverridesAsync_NullOverride_MatchesReadAsync(bool damagedHeader)
    {
        const int blockSize = 512;
        const int blockCount = 128;
        const int entryCount = 128;
        var disk = damagedHeader
            ? CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, 60UL)
            : GptReaderTests.CreateDisk(blockSize);
        if (damagedHeader)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks,
                blockSize, blockCount, null, null, null, cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadAsync(ReadBlocks, blockSize, blockCount,
                cancellationToken: TestContext.Current.CancellationToken));
        }
        else
        {
            var overridden = await GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, null,
                null, null, cancellationToken: TestContext.Current.CancellationToken);
            var baseline = await GptReader.ReadAsync(ReadBlocks, blockSize, blockCount,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(baseline.Partitions, overridden.Partitions);
            Assert.Equal(baseline.MetadataSha256, overridden.MetadataSha256);
            Assert.Equal(2UL, overridden.PrimaryEntryArrayLba);
            Assert.Equal(baseline.PrimaryEntryArrayLba, overridden.PrimaryEntryArrayLba);
            Assert.Equal(95UL, overridden.BackupEntryArrayLba);
        }

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_PlausibleButWrongOverride_RejectsAfterReadingRequestedLocation()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        const int entryCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var disk = CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, 60UL);

        await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks,
            blockSize, blockCount, 64UL, null, null, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new[] { 1L * blockSize, (long)(blockCount - 1) * blockSize, 64L * blockSize, 95L * blockSize },
            reads.Select(read => read.Offset));
        Assert.DoesNotContain(60L * blockSize, reads.Select(read => read.Offset));

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(97UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(ulong.MaxValue)]
    public async Task ReadWithPartitionEntryOverridesAsync_OutOfRangeOverride_RejectsBeforeEntryArrayRead(ulong requestedLba)
    {
        const int blockSize = 512;
        const int blockCount = 128;
        const int entryCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var disk = CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, 60UL);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, requestedLba, null, null,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("partitionEntryLba", error.ParamName);
        Assert.Equal(requestedLba, Assert.IsType<ulong>(error.ActualValue));
        Assert.Single(reads);
        Assert.Equal((long)blockSize, reads[0].Offset);
        Assert.Equal(blockSize, reads[0].Length);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_DamagedFirstUsableLba_RejectsCapacityValidOverride()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        const int entryCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var (_, _, firstUsableLba, _) = Geometry(blockSize, entryCount, blockCount);
        var disk = CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, 60UL);
        var header = disk.AsSpan(blockSize, blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)(firstUsableLba - 2));
        FixHeaderCrc(header);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, 60UL, null, null,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("usable storage", error.Message, StringComparison.Ordinal);
        Assert.Single(reads);
        Assert.Equal((long)blockSize, reads[0].Offset);
        Assert.Equal(blockSize, reads[0].Length);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_DisplacedBackupHeaderLocation_RejectsEveryOverride()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        const int entryCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var disk = CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, 60UL);
        var backupHeader = disk.AsSpan((blockCount - 1) * blockSize, blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(backupHeader[72..], 60UL);
        FixHeaderCrc(backupHeader);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, 60UL, null, null,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("usable storage", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { 1L * blockSize, (long)(blockCount - 1) * blockSize }, reads.Select(read => read.Offset));
        Assert.All(reads, read => Assert.Equal(blockSize, read.Length));

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_CancelledAfterHeader_DoesNotReadBackup()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        const int entryCount = 128;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reads = new List<(long Offset, int Length)>();
        var disk = CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, 60UL);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GptReader.ReadWithPartitionEntryOverridesAsync(
            ReadBlocks, blockSize, blockCount, 60UL, null, null, cancellation.Token));

        Assert.Single(reads);
        Assert.Equal((long)blockSize, reads[0].Offset);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(513, 128UL, "blockSize")]
    [InlineData(256, 128UL, "blockSize")]
    [InlineData(131072, 128UL, "blockSize")]
    [InlineData(512, 5UL, "blockCount")]
    [InlineData(512, ulong.MaxValue, "blockCount")]
    public async Task ReadWithPartitionEntryOverridesAsync_UnsupportedGeometry_RejectsBeforeAnyRead(int blockSize,
        ulong blockCount, string paramName)
    {
        var reads = new List<(long Offset, int Length)>();

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, 2UL, null, null,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(paramName, error.ParamName);
        Assert.Empty(reads);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_FourKiBGeometry_RecoversDerivedOverrideLocation()
    {
        const int blockSize = 4096;
        const int blockCount = 128;
        const int entryCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var (arrayBytes, arrayBlocks, firstUsableLba, backupArrayLba) = Geometry(blockSize, entryCount, blockCount);
        var relocatedLba = (ulong)(backupArrayLba - arrayBlocks - 1);
        var disk = CreateDiskWithDisplacedArray(blockSize, blockCount, entryCount, relocatedLba);
        var table = await GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, relocatedLba,
            null, null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(relocatedLba > (ulong)firstUsableLba);
        Assert.True(relocatedLba + (ulong)arrayBlocks <= (ulong)backupArrayLba);
        Assert.Equal(relocatedLba, table.PrimaryEntryArrayLba);
        Assert.Equal((ulong)backupArrayLba, table.BackupEntryArrayLba);
        Assert.Equal(blockSize, table.BlockSize);
        Assert.Equal((ulong)blockCount, table.BlockCount);
        var partition = Assert.Single(table.Partitions);
        Assert.Equal("boot", partition.Name);
        Assert.Equal(40UL, partition.FirstLba);
        Assert.Equal(43UL, partition.LastLba);
        Assert.Equal(40L * blockSize, partition.ByteOffset);
        Assert.Equal(4L * blockSize, partition.ByteLength);
        Assert.Equal(new[]
        {
            1L * blockSize,
            (long)(blockCount - 1) * blockSize,
            (long)relocatedLba * blockSize,
            (long)backupArrayLba * blockSize
        }, reads.Select(read => read.Offset));
        Assert.Equal(new[] { blockSize, blockSize, arrayBytes, arrayBytes }, reads.Select(read => read.Length));

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_DamagedPrimaryEntryCount_RescuesByOverride()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var disk = GptReaderTests.CreateDisk(blockSize);
        var healthy = await GptReader.ReadAsync(ReadBlocks, blockSize, blockCount,
            cancellationToken: TestContext.Current.CancellationToken);
        var healthyReads = reads.ToArray();
        reads.Clear();

        var header = disk.AsSpan(blockSize, blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 0u);
        FixHeaderCrc(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadAsync(ReadBlocks, blockSize, blockCount,
            cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks,
            blockSize, blockCount, null, null, null, cancellationToken: TestContext.Current.CancellationToken));
        reads.Clear();

        var table = await GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, null, 128u,
            null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(healthyReads, reads);
        Assert.Equal(healthy.Partitions, table.Partitions);
        Assert.Equal(128u, table.PartitionEntryCount);
        var partition = Assert.Single(table.Partitions);
        Assert.Equal("boot", partition.Name);
        Assert.Equal(40L * blockSize, partition.ByteOffset);
        Assert.Equal(4L * blockSize, partition.ByteLength);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_DamagedPrimaryEntrySize_RescuesByOverride()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var disk = GptReaderTests.CreateDisk(blockSize);
        var healthy = await GptReader.ReadAsync(ReadBlocks, blockSize, blockCount,
            cancellationToken: TestContext.Current.CancellationToken);
        var healthyReads = reads.ToArray();
        reads.Clear();

        var header = disk.AsSpan(blockSize, blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 256u);
        FixHeaderCrc(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadAsync(ReadBlocks, blockSize, blockCount,
            cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks,
            blockSize, blockCount, null, null, null, cancellationToken: TestContext.Current.CancellationToken));
        reads.Clear();

        var table = await GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, null, null,
            128u, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(healthyReads, reads);
        Assert.Equal(healthy.Partitions, table.Partitions);
        Assert.Equal(128u, table.PartitionEntrySize);
        var partition = Assert.Single(table.Partitions);
        Assert.Equal("boot", partition.Name);
        Assert.Equal(40L * blockSize, partition.ByteOffset);
        Assert.Equal(4L * blockSize, partition.ByteLength);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_WrongEntrySizeOverride_FailsDeclaredArrayCrcBeforeReturningTable()
    {
        const int blockSize = 512;
        const int blockCount = 256;
        var reads = new List<(long Offset, int Length)>();
        var disk = CreateSpacedDisk();

        await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks,
            blockSize, blockCount, null, null, 256u, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new[]
        {
            1L * blockSize,
            (long)(blockCount - 1) * blockSize,
            2L * blockSize,
            185L * blockSize
        }, reads.Select(read => read.Offset));
        Assert.Equal(new[] { blockSize, blockSize, 32768, 32768 }, reads.Select(read => read.Length));

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_EntryCountOverride_ChangesBothArrayReadLengths()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        var disk = GptReaderTests.CreateDisk(blockSize);
        foreach (var lba in new[] { 1, blockCount - 1 })
        {
            var header = disk.AsSpan(lba * blockSize, blockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[88..],
                Crc32.HashToUInt32(disk.AsSpan(2 * blockSize, 64 * EntrySizeBytes)));
            FixHeaderCrc(header);
        }

        var reads = new List<(long Offset, int Length)>();
        var table = await GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, null, 64u,
            null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(64u, table.PartitionEntryCount);
        Assert.Equal(128u, table.PartitionEntrySize);
        var partition = Assert.Single(table.Partitions);
        Assert.Equal("boot", partition.Name);
        Assert.Equal(new[]
        {
            1L * blockSize,
            (long)(blockCount - 1) * blockSize,
            2L * blockSize,
            95L * blockSize
        }, reads.Select(read => read.Offset));
        Assert.Equal(new[] { blockSize, blockSize, 64 * EntrySizeBytes, 64 * EntrySizeBytes },
            reads.Select(read => read.Length));

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(65537u)]
    [InlineData(uint.MaxValue)]
    public async Task ReadWithPartitionEntryOverridesAsync_OutOfRangeEntryCount_ThrowsArgumentOutOfRange(uint entryCount)
    {
        const int blockSize = 512;
        const int blockCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var disk = GptReaderTests.CreateDisk(blockSize);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, null, entryCount, null,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("partitionEntryCount", error.ParamName);
        Assert.Equal(entryCount, Assert.IsType<uint>(error.ActualValue));
        Assert.Single(reads);
        Assert.Equal((long)blockSize, reads[0].Offset);
        Assert.Equal(blockSize, reads[0].Length);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(127u)]
    [InlineData(129u)]
    [InlineData(384u)]
    [InlineData(4097u)]
    [InlineData(65536u)]
    public async Task ReadWithPartitionEntryOverridesAsync_OutOfRangeEntrySize_ThrowsArgumentOutOfRange(uint entrySize)
    {
        const int blockSize = 512;
        const int blockCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var disk = GptReaderTests.CreateDisk(blockSize);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, null, null, entrySize,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("partitionEntrySize", error.ParamName);
        Assert.Equal(entrySize, Assert.IsType<uint>(error.ActualValue));
        Assert.Single(reads);
        Assert.Equal((long)blockSize, reads[0].Offset);
        Assert.Equal(blockSize, reads[0].Length);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadWithPartitionEntryOverridesAsync_OverriddenDimensionsBeyondMetadataCap_ThrowsArgumentOutOfRange()
    {
        const int blockSize = 512;
        const int blockCount = 128;
        var reads = new List<(long Offset, int Length)>();
        var disk = GptReaderTests.CreateDisk(blockSize);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocks, blockSize, blockCount, null, 65536u, 4096u,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("partitionEntryCount", error.ParamName);
        Assert.Single(reads);
        Assert.Equal((long)blockSize, reads[0].Offset);
        Assert.Equal(blockSize, reads[0].Length);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    private static byte[] CreateDiskWithDisplacedArray(int blockSize, int blockCount, int entryCount, ulong targetLba)
    {
        var disk = GptReaderTests.CreateDisk(blockSize, entryCount, blockCount);
        var (arrayBytes, arrayBlocks, firstUsableLba, _) = Geometry(blockSize, entryCount, blockCount);
        disk.AsSpan(2 * blockSize, arrayBytes).CopyTo(disk.AsSpan((int)targetLba * blockSize));
        var header = disk.AsSpan(blockSize, blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], (ulong)firstUsableLba - (ulong)arrayBlocks + 1);
        FixHeaderCrc(header);
        return disk;
    }

    private static byte[] CreateSpacedDisk()
    {
        const int blockSize = 512;
        const int blockCount = 256;
        const int entryCount = 128;
        var disk = new byte[blockSize * blockCount];
        var entries = disk.AsSpan(2 * blockSize, entryCount * EntrySizeBytes);
        new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4").TryWriteBytes(entries[..16]);
        new Guid("11111111-2222-3333-4444-555555555555").TryWriteBytes(entries[16..32]);
        BinaryPrimitives.WriteUInt64LittleEndian(entries[32..], 70);
        BinaryPrimitives.WriteUInt64LittleEndian(entries[40..], 73);
        Encoding.Unicode.GetBytes("boot", entries[56..]);
        entries.CopyTo(disk.AsSpan(185 * blockSize));
        var arrayCrc = Crc32.HashToUInt32(entries);
        foreach (var (lba, arrayLba) in new[] { (1, 2UL), (blockCount - 1, 185UL) })
        {
            var header = disk.AsSpan(lba * blockSize, blockSize);
            "EFI PART"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x10000);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
            BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)lba);
            BinaryPrimitives.WriteUInt64LittleEndian(header[32..], lba == 1 ? (ulong)(blockCount - 1) : 1UL);
            BinaryPrimitives.WriteUInt64LittleEndian(header[40..], 70);
            BinaryPrimitives.WriteUInt64LittleEndian(header[48..], 180);
            new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").TryWriteBytes(header[56..72]);
            BinaryPrimitives.WriteUInt64LittleEndian(header[72..], arrayLba);
            BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)entryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 128);
            BinaryPrimitives.WriteUInt32LittleEndian(header[88..], arrayCrc);
            FixHeaderCrc(header);
        }
        return disk;
    }

    private static (int ArrayBytes, int ArrayBlocks, int FirstUsableLba, int BackupArrayLba) Geometry(int blockSize,
        int entryCount, int blockCount)
    {
        var arrayBytes = entryCount * EntrySizeBytes;
        var arrayBlocks = (Math.Max(arrayBytes, MinimumArrayBytes) + blockSize - 1) / blockSize;
        return (arrayBytes, arrayBlocks, 2 + arrayBlocks, blockCount - 1 - arrayBlocks);
    }

    private static void FixHeaderCrc(Span<byte> header)
    {
        header[16..20].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.HashToUInt32(header[..92]));
    }
}