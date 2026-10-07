using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsParserStreamingTests
{
    private static byte[] BuildImage(TempDirectory temp, params ReadOnlySpan<(string Path, byte[] Content)> files)
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

        new ErofsInode
        {
            Advise = ErofsInode.DataLayoutFlat,
            Mode = ErofsInode.ModeDirectory,
            Size = blockSize,
            U = 3,
            Nlink = 2,
        }.WriteTo(image.AsSpan(blockSize));

        new ErofsInode
        {
            Advise = ErofsInode.DataLayoutCompressed | ErofsInode.FlagExtended,
            Mode = ErofsInode.ModeRegularFile,
            Size = (ulong)payload.Length,
            U = 1,
            Nlink = 1,
        }.WriteTo(image.AsSpan(blockSize + ErofsInode.CompactSize));

        var inodeLoc = blockSize + ErofsInode.CompactSize;
        var headerOffset = inodeLoc + ErofsInode.ExtendedSize;
        var entryOffset = headerOffset + 8 + 8;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOffset), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOffset + 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 4), 2);

        var dataOffset = 2 * blockSize;
        compressed.AsSpan(0, compressedLength).CopyTo(image.AsSpan(dataOffset + blockSize - compressedLength));

        var dirOffset = 3 * blockSize;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(dirOffset), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dirOffset + 8), ErofsDirectoryEntry.DirentSize);
        image[dirOffset + 10] = ErofsInode.FileTypeRegularFile;
        image[dirOffset + ErofsDirectoryEntry.DirentSize] = (byte)'x';

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("x"));
    }

    [Fact]
    public void Parser_RejectsInvalidBlockSizeShift()
    {
        var image = new byte[ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ErofsSuperblock.SuperblockOffset), ErofsSuperblock.Magic);
        image[ErofsSuperblock.SuperblockOffset + 12] = 31;

        Assert.Throws<FormatException>(() => new ErofsParser(image));
    }

    [Fact]
    public void PclusterCount_OverflowingSize_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<FileSystemCorruptedException>(() => ErofsCompression.PclusterCount(ulong.MaxValue, 4096));
        Assert.Throws<FileSystemCorruptedException>(() => ErofsCompression.PclusterCount((ulong)int.MaxValue * 4096 + 1, 4096));
        Assert.Equal(0, ErofsCompression.PclusterCount(0, 4096));
        Assert.Equal(1, ErofsCompression.PclusterCount(1, 4096));
        Assert.Equal(2, ErofsCompression.PclusterCount(4097, 4096));
    }
}