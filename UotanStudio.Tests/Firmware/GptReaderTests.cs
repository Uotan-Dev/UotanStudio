using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BlockImage.Gpt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class GptReaderTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Read_MatchingTables_ReturnsCheckedRangesAndRestoresPosition(int blockSize)
    {
        using var source = new MemoryStream(CreateDisk(blockSize));
        source.Position = 17;
        var table = GptReader.Read(source, blockSize, 128, TestContext.Current.CancellationToken);
        var partition = Assert.Single(table.Partitions);
        Assert.Equal("boot", partition.Name);
        Assert.Equal(40L * blockSize, partition.ByteOffset);
        Assert.Equal(4L * blockSize, partition.ByteLength);
        Assert.Equal(17, source.Position);
        Assert.Same(partition, table.FindPartition("boot"));
        Assert.Throws<KeyNotFoundException>(() => table.FindPartition("BOOT"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(56)]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(84)]
    [InlineData(88)]
    [InlineData(92)]
    public void Read_DamagedPrimaryHeader_RejectsAndRestoresPosition(int field)
    {
        var disk = CreateDisk(512);
        disk[512 + field] ^= 0xFF;
        using var source = new MemoryStream(disk);
        source.Position = 11;
        Assert.Throws<InvalidDataException>(() => GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken));
        Assert.Equal(11, source.Position);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(24, 2)]
    [InlineData(32, 126)]
    [InlineData(40, 2)]
    [InlineData(48, 127)]
    [InlineData(72, 40)]
    [InlineData(80, 65537)]
    [InlineData(84, 192)]
    [InlineData(84, 8192)]
    public void Read_InvalidHeaderWithValidCrc_Rejects(int offset, ulong value)
    {
        var disk = CreateDisk(512);
        if (offset >= 80)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(512 + offset), (uint)value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(512 + offset), value);
        }
        FixHeader(disk.AsSpan(512, 512));
        using var source = new MemoryStream(disk);
        Assert.Throws<InvalidDataException>(() => GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_DamagedBackup_RejectsWithoutPrimaryFallback()
    {
        var disk = CreateDisk(512);
        disk[127 * 512] = 0;
        using var source = new MemoryStream(disk);
        Assert.Throws<InvalidDataException>(() => GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_DifferentArraysWithValidIndividualCrcs_Rejects()
    {
        var disk = CreateDisk(512);
        disk[95 * 512 + 56] = (byte)'x';
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(127 * 512 + 88),
            Crc32.HashToUInt32(disk.AsSpan(95 * 512, 16384)));
        FixHeader(disk.AsSpan(127 * 512, 512));
        using var source = new MemoryStream(disk);
        Assert.Throws<InvalidDataException>(() => GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_MatchingCorruptedArrays_RejectsCrc()
    {
        var disk = CreateDisk(512);
        disk[2 * 512 + 56] ^= 1;
        disk[95 * 512 + 56] ^= 1;
        using var source = new MemoryStream(disk);
        Assert.Throws<InvalidDataException>(() => GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("duplicate-id")]
    [InlineData("empty-id")]
    [InlineData("outside")]
    [InlineData("reversed")]
    [InlineData("reserved")]
    [InlineData("utf16")]
    public void Read_InvalidEntriesWithValidCrcs_Rejects(string mutation)
    {
        var disk = CreateDisk(512);
        var entries = disk.AsSpan(1024, 16384);
        switch (mutation)
        {
            case "overlap":
            case "duplicate-id":
                entries[..128].CopyTo(entries[128..]);
                if (mutation == "overlap")
                {
                    Guid.NewGuid().TryWriteBytes(entries[144..160]);
                }
                else
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(entries[160..], 50);
                    BinaryPrimitives.WriteUInt64LittleEndian(entries[168..], 53);
                }
                break;
            case "empty-id":
                entries[16..32].Clear();
                break;
            case "outside":
                BinaryPrimitives.WriteUInt64LittleEndian(entries[40..], 95);
                break;
            case "reversed":
                BinaryPrimitives.WriteUInt64LittleEndian(entries[32..], 44);
                break;
            case "reserved":
                entries[48] = 8;
                break;
            case "utf16":
                entries[56] = 0;
                entries[57] = 0xD8;
                break;
        }
        FixArrays(disk);
        using var source = new MemoryStream(disk);
        Assert.Throws<InvalidDataException>(() => GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FindPartition_DuplicateNames_RejectsAmbiguousLookup()
    {
        var disk = CreateDisk(512);
        disk.AsSpan(1024, 128).CopyTo(disk.AsSpan(1152));
        Guid.NewGuid().TryWriteBytes(disk.AsSpan(1168, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(1184), 50);
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(1192), 53);
        FixArrays(disk);
        using var source = new MemoryStream(disk);
        var table = GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken);
        Assert.Equal(2, table.Partitions.Count);
        Assert.Throws<InvalidDataException>(() => table.FindPartition("boot"));
    }

    [Fact]
    public void Read_TruncatedOrCancelledInput_PreservesPosition()
    {
        using var truncated = new MemoryStream(CreateDisk(512)[..^1]);
        Assert.Throws<InvalidDataException>(() => GptReader.Read(truncated, 512, 128, TestContext.Current.CancellationToken));
        using var source = new MemoryStream(CreateDisk(512));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        source.Position = 19;
        Assert.Throws<OperationCanceledException>(() => GptReader.Read(source, 512, 128, cancellation.Token));
        Assert.Equal(19, source.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => GptReader.Read(source, 513, 128, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => GptReader.Read(source, 512, ulong.MaxValue, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(65536)]
    public async Task ReadAsync_MatchingTables_UsesOnlyBoundedAlignedMetadataReads(int blockSize)
    {
        var disk = CreateDisk(blockSize);
        var reads = new List<(long Offset, int Length)>();
        var table = await GptReader.ReadAsync(ReadBlocks, blockSize, 128, TestContext.Current.CancellationToken);
        Assert.Equal("boot", Assert.Single(table.Partitions).Name);
        Assert.Equal(4, reads.Count);
        Assert.All(reads, read =>
        {
            Assert.Equal(0, read.Offset % blockSize);
            Assert.Equal(0, read.Length % blockSize);
            Assert.InRange(read.Length, blockSize, 65536);
        });
        Assert.Equal((long)blockSize, reads[0].Offset);
        Assert.Equal(127L * blockSize, reads[1].Offset);
        Assert.Equal(2L * blockSize, reads[2].Offset);

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
    public async Task ReadAsync_DamagedHeaderOrArray_Rejects(bool array)
    {
        var disk = CreateDisk(512);
        disk[array ? 1024 + 56 : 512] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => GptReader.ReadAsync(ReadBlocks, 512, 128,
            TestContext.Current.CancellationToken));

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadAsync_CancelledAfterHeader_DoesNotReadBackup()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var disk = CreateDisk(512);
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GptReader.ReadAsync(ReadBlocks, 512, 128, cancellation.Token));
        Assert.Equal(1, count);

        ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            count++;
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadAsync_MultipleBatchesAndDifferentPadding_MatchesSynchronousResult()
    {
        const int blockCount = 2048;
        const int entryCount = 1025;
        const int arrayBytes = entryCount * 128;
        var disk = CreateDisk(512, entryCount, blockCount);
        var arrayBlocks = (arrayBytes + 511) / 512;
        var backupStart = (blockCount - 1 - arrayBlocks) * 512;
        disk.AsSpan(backupStart + arrayBytes, arrayBlocks * 512 - arrayBytes).Fill(0xA5);
        using var stream = new MemoryStream(disk);
        var expected = GptReader.Read(stream, 512, blockCount, TestContext.Current.CancellationToken);
        var reads = new List<(long Offset, int Length)>();

        var actual = await GptReader.ReadAsync(ReadBlocks, 512, blockCount, TestContext.Current.CancellationToken);

        Assert.Equal(expected.Partitions, actual.Partitions);
        Assert.Equal(expected.MetadataSha256, actual.MetadataSha256);
        Assert.Equal(8, reads.Count);
        Assert.Equal(new[] { 512, 512, 65536, 65536, 65536, 65536, 512, 512 },
            reads.Select(read => read.Length));
        Assert.Equal(1024 + 131072, reads[^2].Offset);
        Assert.Equal(backupStart + 131072, reads[^1].Offset);

        async ValueTask ReadBlocks(long offset, Memory<byte> destination, CancellationToken token)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            reads.Add((offset, destination.Length));
            disk.AsMemory((int)offset, destination.Length).CopyTo(destination);
        }
    }

    [Fact]
    public void Read_MetadataFingerprint_CoversBothHeaderBlocksAndExactArray()
    {
        var disk = CreateDisk(512);
        using var stream = new MemoryStream(disk);
        var table = GptReader.Read(stream, 512, 128, TestContext.Current.CancellationToken);
        var expected = SHA256.HashData([
            .. SHA256.HashData(disk.AsSpan(512, 512)),
            .. SHA256.HashData(disk.AsSpan(127 * 512, 512)),
            .. disk.AsSpan(1024, 16384)
        ]);
        Assert.Equal(Convert.ToHexString(expected), table.MetadataSha256);

        foreach (var lba in new[] { 1, 127 })
        {
            BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(lba * 512 + 40), 35);
            FixHeader(disk.AsSpan(lba * 512, 512));
        }
        var changed = GptReader.Read(stream, 512, 128, TestContext.Current.CancellationToken);
        Assert.Equal(table.Partitions, changed.Partitions);
        Assert.NotEqual(table.MetadataSha256, changed.MetadataSha256);
    }

    internal static byte[] CreateDisk(int blockSize, int entryCount = 128, int blockCount = 128)
    {
        var disk = new byte[blockSize * blockCount];
        var entries = disk.AsSpan(blockSize * 2, entryCount * 128);
        var arrayBlocks = (Math.Max(entries.Length, 16384) + blockSize - 1) / blockSize;
        var firstPartition = (ulong)Math.Max(40, 2 + arrayBlocks);
        new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4").TryWriteBytes(entries[..16]);
        new Guid("11111111-2222-3333-4444-555555555555").TryWriteBytes(entries[16..32]);
        BinaryPrimitives.WriteUInt64LittleEndian(entries[32..], firstPartition);
        BinaryPrimitives.WriteUInt64LittleEndian(entries[40..], firstPartition + 3);
        Encoding.Unicode.GetBytes("boot", entries[56..]);
        var backupLba = blockCount - 1 - arrayBlocks;
        entries.CopyTo(disk.AsSpan(backupLba * blockSize));
        foreach (var lba in new[] { 1, blockCount - 1 })
        {
            var header = disk.AsSpan(lba * blockSize, blockSize);
            "EFI PART"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x10000);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
            BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)lba);
            BinaryPrimitives.WriteUInt64LittleEndian(header[32..], lba == 1 ? (ulong)(blockCount - 1) : 1UL);
            BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)(2 + arrayBlocks));
            BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)(backupLba - 1));
            new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").TryWriteBytes(header[56..72]);
            BinaryPrimitives.WriteUInt64LittleEndian(header[72..], lba == 1 ? 2UL : (ulong)backupLba);
            BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)entryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 128);
            BinaryPrimitives.WriteUInt32LittleEndian(header[88..], Crc32.HashToUInt32(entries));
            FixHeader(header);
        }
        return disk;
    }

    internal static void FixArrays(byte[] disk)
    {
        var entries = disk.AsSpan(1024, 16384);
        entries.CopyTo(disk.AsSpan(95 * 512));
        foreach (var lba in new[] { 1, 127 })
        {
            var header = disk.AsSpan(lba * 512, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(header[88..], Crc32.HashToUInt32(entries));
            FixHeader(header);
        }
    }

    private static void FixHeader(Span<byte> header)
    {
        header[16..20].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.HashToUInt32(header[..92]));
    }
}