using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Build;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Writing;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Split;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class FirmwarePipelineRegressionTests
{
    private const uint LogicalBlock = 4096;

    [Fact]
    public async Task Ext4GrowToLpToSparseToChunksAndBack_PreservesContentExtentsAndRawBytes()
    {
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();

        var root = Path.Combine(temp.Path, "tree");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "hello pipeline\n", token);
        var blob = new byte[64 * 1024 + 77];
        for (var i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)(i * 37 + 11);
        }
        File.WriteAllBytes(Path.Combine(root, "blob.bin"), blob);
        Directory.CreateDirectory(Path.Combine(root, "subdir"));
        await File.WriteAllTextAsync(Path.Combine(root, "subdir", "inner.txt"), "inner pipeline\n", token);

        var systemImagePath = Path.Combine(temp.Path, "system.img");
        await File.WriteAllBytesAsync(systemImagePath,
            Ext4Builder.Build(root, new Ext4Options { TargetSizeBytes = 8 * 1024 * 1024 }), token);
        var (blocksBefore, blockSize) = ReadExt4Geometry(systemImagePath);
        Assert.Equal(LogicalBlock, blockSize);
        Assert.True(blocksBefore > 0);

        Ext4Builder.Grow(systemImagePath, blocksBefore * 2);
        var (blocksAfter, _) = ReadExt4Geometry(systemImagePath);
        Assert.True(blocksAfter >= blocksBefore * 2);
        Assert.Equal(new FileInfo(systemImagePath).Length, checked(blocksAfter * blockSize));

        using (var parser = new Ext4Parser(File.OpenRead(systemImagePath)))
        {
            Assert.Equal("hello pipeline\n", Encoding.UTF8.GetString(ReadExt4File(parser, "hello.txt")));
            Assert.Equal(blob, ReadExt4File(parser, "blob.bin"));
            Assert.Equal("inner pipeline\n", Encoding.UTF8.GetString(ReadExt4File(parser, "subdir/inner.txt")));
        }

        var grownSize = (ulong)new FileInfo(systemImagePath).Length;
        var deviceSize = grownSize + 16UL * 1024 * 1024;
        var builder = MetadataBuilder.New(deviceSize, 64 * 1024, 3, LogicalBlock);
        builder.AddPartition("system", "default", grownSize);
        builder.AddPartition("vendor", "default", 0);
        var metadata = builder.Build();

        var superImagePath = Path.Combine(temp.Path, "super.img");
        MetadataWriter.WriteToImageFile(superImagePath, metadata);
        Assert.Equal(checked((long)deviceSize), new FileInfo(superImagePath).Length);

        var dataStart = checked((long)metadata.BlockDevices[0].FirstLogicalSector * MetadataFormat.LP_SECTOR_SIZE);
        using (var super = new FileStream(superImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            super.Position = dataStart;
            using var ext4 = File.OpenRead(systemImagePath);
            ext4.CopyTo(super);
        }

        var superSparsePath = Path.Combine(temp.Path, "super.sparse.img");
        SparseImageConverter.ConvertRawToSparse(superImagePath, superSparsePath, LogicalBlock,
            new SparseWriteOptions { BlockSize = LogicalBlock });

        SparseImageConverter.ResparseImage(superSparsePath,
            Path.Combine(temp.Path, "super.img.sparsechunk"), 64 * 1024);
        var pieces = Directory.GetFiles(temp.Path, "super.img.sparsechunk.??")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(pieces.Length > 1);
        var set = SparseChunkSplitter.Inspect(pieces[0]);
        Assert.Equal(SparseChunkVariant.SelfContainedSparse, set.Variant);

        var mergedPath = Path.Combine(temp.Path, "super.merged.sparse.img");
        await SparseChunkSplitter.MergeAsync(pieces[1], mergedPath,
            cancellationToken: token);

        using var mergedSparse = SparseReader.FromImageFile(mergedPath, validateCrc: false);
        using var expanded = new MemoryStream();
        mergedSparse.WriteRawToStream(expanded);
        Assert.Equal(await File.ReadAllBytesAsync(superImagePath, token), expanded.ToArray());

        expanded.Position = 0;
        var readBack = MetadataReader.ReadFromImageStream(expanded);
        Assert.Equal(metadata.Partitions.Select(p => p.NameString), readBack.Partitions.Select(p => p.NameString));
        Assert.Equal(metadata.Extents.Select(e => (e.NumSectors, e.TargetType, e.TargetData, e.TargetSource)),
            readBack.Extents.Select(e => (e.NumSectors, e.TargetType, e.TargetData, e.TargetSource)));
        Assert.Equal(metadata.Groups.Select(g => g.NameString), readBack.Groups.Select(g => g.NameString));
        Assert.Equal(metadata.BlockDevices[0].Size, readBack.BlockDevices[0].Size);
        Assert.Equal(metadata.Geometry.LogicalBlockSize, readBack.Geometry.LogicalBlockSize);
        Assert.Equal(metadata.Geometry.MetadataMaxSize, readBack.Geometry.MetadataMaxSize);
        Assert.Equal(metadata.Geometry.MetadataSlotCount, readBack.Geometry.MetadataSlotCount);

        using var superStream = File.OpenRead(superImagePath);
        MetadataReader.ReadPrimaryGeometry(superStream, out var primaryGeometry);
        superStream.Position = 0;
        var primarySlot = MetadataReader.ReadPrimaryMetadata(superStream, primaryGeometry, 0);
        Assert.Equal(metadata.Extents.Select(e => e.TargetData),
            primarySlot.Extents.Select(e => e.TargetData));
    }

    private static (long Blocks, uint BlockSize) ReadExt4Geometry(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> superblock = stackalloc byte[1024];
        stream.Position = 1024;
        stream.ReadExactly(superblock);
        var blockSize = 1024u << (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.Slice(24, 4));
        var blocks = BinaryPrimitives.ReadUInt32LittleEndian(superblock.Slice(4, 4));
        return (blocks, blockSize);
    }

    private static byte[] ReadExt4File(Ext4Parser parser, string path)
    {
        using var stream = new MemoryStream();
        parser.ReadFile(path, stream);
        return stream.ToArray();
    }
}