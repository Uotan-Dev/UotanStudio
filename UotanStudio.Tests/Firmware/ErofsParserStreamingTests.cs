using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsParserStreamingTests
{
    private static byte[] BuildImage(TempDirectory temp, params (string Path, byte[] Content)[] files)
    {
        foreach (var file in files)
        {
            temp.WriteBytes(file.Path, file.Content);
        }

        return new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
    }

    [Fact]
    public void StreamConstructor_NonzeroBase_RoundTrips()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("based payload");
        var image = BuildImage(temp, ("file.txt", payload));
        using var stream = new MemoryStream();
        stream.Write(new byte[137]);
        stream.Write(image);
        stream.Position = 137;

        using var parser = new ErofsParser(stream);

        Assert.Equal(image.Length, parser.ImageSize);
        Assert.Equal(payload, parser.ReadFileBytes("file.txt"));
    }

    [Fact]
    public void FilePathConstructor_WhenValidationFails_ReleasesFileHandle()
    {
        using var temp = new TempDirectory();
        var imagePath = Path.Combine(temp.Path, "invalid.erofs");
        File.WriteAllBytes(imagePath, new byte[4096]);

        Assert.Throws<FormatException>(() => new ErofsParser(imagePath));
        File.Delete(imagePath);

        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public async Task SharedSeekableStream_ConcurrentReadsRemainConsistent()
    {
        using var temp = new TempDirectory();
        var first = new byte[2 * 1024 * 1024];
        var second = new byte[2 * 1024 * 1024];
        new Random(17).NextBytes(first);
        new Random(29).NextBytes(second);
        var image = BuildImage(temp, ("first.bin", first), ("second.bin", second));
        using var stream = new MemoryStream(image, writable: false);
        using var parser = new ErofsParser(stream);
        _ = parser.EnumerateFiles();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await Task.Yield();
            var expected = i % 2 == 0 ? first : second;
            Assert.Equal(expected, parser.ReadFileBytes(i % 2 == 0 ? "first.bin" : "second.bin"));
        }));
    }

    [Fact]
    public void ExtractToDirectory_OverwriteFalse_PreservesExistingTarget()
    {
        using var temp = new TempDirectory();
        var image = BuildImage(temp, ("file.txt", Encoding.UTF8.GetBytes("image payload")));
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "file.txt"), "existing", new UTF8Encoding(false));

        using var parser = new ErofsParser(image);
        parser.ExtractToDirectory(output, overwrite: false);

        Assert.Equal("existing", File.ReadAllText(Path.Combine(output, "file.txt")));
    }

    [Fact]
    public void Parser_ExtendedCompressedInode_ReadsIndexTableAfter64ByteInode()
    {
        // Hand-crafted image where a compressed file uses an extended (64-byte) inode, so the
        // pcluster index table starts at inode offset + 64 (per the kernel's erofs_fs.h layout).
        // 手工构造镜像：压缩文件使用 extended（64 字节）inode，故 pcluster 索引表起始于 inode 偏移 + 64。
        const int blockSize = 4096;
        var payload = Encoding.UTF8.GetBytes("hello erofs");
        var maxOutput = ErofsCompression.MaximumOutputSize(payload.Length);
        var compressed = new byte[maxOutput];
        var compressedLength = ErofsCompression.Compress(payload, 0, payload.Length, compressed, 0, maxOutput, 1);
        Assert.InRange(compressedLength, 1, maxOutput);

        var image = new byte[blockSize * 4];
        new ErofsSuperblock
        {
            MagicValue = ErofsSuperblock.Magic,
            BlockSizeBits = 12,
            RootNid = 0,
            Inos = 2,
            Blocks = 4,
            MetaBlkAddr = 1,
        }.WriteTo(image.AsSpan(ErofsSuperblock.SuperblockOffset));

        // Root directory (compact, flat) at metadata offset 0; directory data at block 3.
        new ErofsInode
        {
            Advise = ErofsInode.DataLayoutFlat,
            Mode = ErofsInode.ModeDirectory,
            Size = blockSize,
            U = 3,
            Nlink = 2,
        }.WriteTo(image.AsSpan(blockSize));

        // File inode (extended + compressed full) at metadata offset 32: i_format == 0b0011.
        new ErofsInode
        {
            Advise = ErofsInode.DataLayoutCompressed | ErofsInode.FlagExtended,
            Mode = ErofsInode.ModeRegularFile,
            Size = (ulong)payload.Length,
            U = 1, // one compressed pcluster
            Nlink = 1,
        }.WriteTo(image.AsSpan(blockSize + ErofsInode.CompactSize));

        // Compressed pcluster index table right after the 64-byte extended inode (offset 32 + 64).
        new PclusterIndex(2, (ushort)compressedLength, 0)
            .WriteTo(image.AsSpan(blockSize + ErofsInode.CompactSize + ErofsInode.ExtendedSize));

        // Compressed data at block 2.
        compressed.AsSpan(0, compressedLength).CopyTo(image.AsSpan(2 * blockSize));

        // Directory block at block 3 with a single "x" entry (nid 32).
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(3 * blockSize), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(3 * blockSize + 8), (ushort)(blockSize - 1));
        image[3 * blockSize + 10] = ErofsInode.FileTypeRegularFile;
        image[4 * blockSize - 1] = (byte)'x';

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("x"));
    }

    [Fact]
    public void Parser_RejectsInvalidBlockSizeShift()
    {
        var image = new byte[ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ErofsSuperblock.SuperblockOffset), ErofsSuperblock.Magic);
        image[ErofsSuperblock.SuperblockOffset + 12] = 31; // blkszbits → 1 << 31 is not a valid size

        Assert.Throws<FormatException>(() => new ErofsParser(image));
    }

    [Fact]
    public void PclusterCount_OverflowingSize_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<InvalidDataException>(() => ErofsCompression.PclusterCount(ulong.MaxValue, 4096));
        Assert.Throws<InvalidDataException>(() => ErofsCompression.PclusterCount((ulong)int.MaxValue * 4096 + 1, 4096));
        Assert.Equal(0, ErofsCompression.PclusterCount(0, 4096));
        Assert.Equal(1, ErofsCompression.PclusterCount(1, 4096));
        Assert.Equal(2, ErofsCompression.PclusterCount(4097, 4096));
    }
}