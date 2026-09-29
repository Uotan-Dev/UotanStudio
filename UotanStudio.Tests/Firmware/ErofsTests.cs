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

/// <summary>
/// EROFS 打包 → 解析往返测试（含压缩路径）。
/// <para>EROFS build → parse round-trip tests (including the compressed path).</para>
/// </summary>
public class ErofsTests
{
    /// <summary>
    /// Writes a text file without a UTF-8 BOM (TempDirectory.Write uses Encoding.UTF8 which emits
    /// a BOM, breaking byte-exact round-trip assertions).
    /// <para>写入不带 UTF-8 BOM 的文本文件（TempDirectory.Write 使用会带 BOM 的 Encoding.UTF8，
    /// 破坏逐字节一致的往返断言）。</para>
    /// </summary>
    private static void WriteText(TempDirectory temp, string relativePath, string content)
    {
        var full = System.IO.Path.Combine(temp.Path, relativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    [Fact]
    public void BuildParse_Compressed_RoundTripsFilesAndSubdirectories()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "hello.txt", "hello erofs\n");
        temp.WriteBytes("dir/nested.bin", [0x00, 0x01, 0xFE, 0xFF, 0x80, 0x7F]);
        WriteText(temp, "dir/sub/inner.txt", "inner content");

        var random = new Random(42);
        var big = new byte[20 * 1024];
        random.NextBytes(big);
        temp.WriteBytes("dir/big.bin", big);

        var text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 400));
        WriteText(temp, "dir/text.txt", text);

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true, CompressionLevel = 3 });
        var parser = new ErofsParser(image);

        var paths = parser.EnumerateFiles()
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[] { "dir/big.bin", "dir/nested.bin", "dir/sub/inner.txt", "dir/text.txt", "hello.txt" },
            paths);

        Assert.Equal("hello erofs\n", Encoding.UTF8.GetString(parser.ReadFileBytes("hello.txt")));
        Assert.Equal(big, parser.ReadFileBytes("dir/big.bin"));
        Assert.Equal(new byte[] { 0x00, 0x01, 0xFE, 0xFF, 0x80, 0x7F }, parser.ReadFileBytes("dir/nested.bin"));
        Assert.Equal(text, Encoding.UTF8.GetString(parser.ReadFileBytes("dir/text.txt")));
        Assert.Equal("inner content", Encoding.UTF8.GetString(parser.ReadFileBytes("dir/sub/inner.txt")));
    }

    [Fact]
    public void BuildParse_Uncompressed_RoundTripsContent()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", "alpha");
        WriteText(temp, "b.txt", "beta");

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var parser = new ErofsParser(image);

        var paths = parser.EnumerateFiles()
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "a.txt", "b.txt" }, paths);
        Assert.Equal("alpha", Encoding.UTF8.GetString(parser.ReadFileBytes("a.txt")));
        Assert.Equal("beta", Encoding.UTF8.GetString(parser.ReadFileBytes("b.txt")));
    }

    [Fact]
    public void BuildParse_LargeFile_AcrossMultipleBlocks()
    {
        using var temp = new TempDirectory();
        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef", 2000));
        WriteText(temp, "large.txt", payload);

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true, CompressionLevel = 9 });
        var parser = new ErofsParser(image);

        Assert.Equal(payload, Encoding.UTF8.GetString(parser.ReadFileBytes("large.txt")));
        Assert.Equal(payload.Length, parser.EnumerateFiles().Single().Size);
    }

    [Fact]
    public void BuildParse_MultiBlockDirectory_ReportsExactPayloadSize()
    {
        using var temp = new TempDirectory();
        var names = Enumerable.Range(0, 400).Select(index => $"file_{index:D3}.txt").ToArray();
        foreach (var name in names)
        {
            WriteText(temp, "dir/" + name, "x");
        }

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var parser = new ErofsParser(image);

        var directory = parser.EnumerateEntries().Single(entry => entry.Path == "dir");
        var parsed = parser.EnumerateFiles()
            .Where(entry => entry.Path.StartsWith("dir/", StringComparison.Ordinal))
            .Select(entry => entry.Path["dir/".Length..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal).ToArray(), parsed);
        Assert.Equal(9656, directory.Size);
        Assert.NotEqual(0, directory.Size % parser.BlockSize);
    }

    [Fact]
    public void Superblock_FromImage_ReadsMetaboxExtensionSlot()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "file.txt", "payload");

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false, Metabox = true });
        using var parser = new ErofsParser(image);
        var direct = ErofsSuperblock.FromImage(image);

        Assert.True(parser.Superblock.HasMetabox);
        Assert.True(parser.Superblock.SbExtSlots >= 1);
        Assert.NotEqual(0UL, parser.Superblock.MetaBoxNid);
        Assert.Equal(parser.Superblock.MetaBoxNid, direct.MetaBoxNid);
        Assert.Equal(parser.Superblock.SbExtSlots, direct.SbExtSlots);
    }

    [Fact]
    public void Compress_DefaultPclusterSmallerThanSingleBlockPcluster()
    {
        using var temp = new TempDirectory();
        var text = string.Concat(Enumerable.Repeat("0123456789 abcdefghijklmnopqrstuvwxyz EROFS merge ratio\n", 6000));
        WriteText(temp, "text", text);
        var random = new byte[120_000];
        new Random(913).NextBytes(random);
        temp.WriteBytes("random", random);
        temp.WriteBytes("moderate", Enumerable.Range(0, 40).SelectMany(_ => random.Take(8192)).ToArray());

        byte[] Build(Action<ErofsOptions> configure)
        {
            using var output = new MemoryStream();
            var options = new ErofsOptions { Compress = true, Algorithm = ErofsCompressionAlgorithm.Lz4, CompressionLevel = 9 };
            configure(options);
            new ErofsBuilder().Build(temp.Path, output, options);
            return output.ToArray();
        }

        var singleBlock = Build(o => o.MaxPclusterSize = o.BlockSize);
        var defaultPcluster = Build(o => { });

        Assert.True(defaultPcluster.Length < singleBlock.Length / 2,
            $"default {defaultPcluster.Length} should be well below single-block {singleBlock.Length}.");

        var parser = new ErofsParser(defaultPcluster);
        Assert.Equal(text, Encoding.UTF8.GetString(parser.ReadFileBytes("text")));
        Assert.Equal(random, parser.ReadFileBytes("random"));
    }

    [Fact]
    public void BuildParse_EmptyDirectory_ReturnsNoFiles()
    {
        using var temp = new TempDirectory();

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var parser = new ErofsParser(image);

        Assert.Empty(parser.EnumerateFiles());
    }

    [Fact]
    public void Parser_RejectsImageWithoutMagic()
    {
        Assert.Throws<FormatException>(() => new ErofsParser(new byte[4096]));
    }

    [Fact]
    public async Task FormatHandler_CanHandleAndReadMetadata()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "x.txt", "payload");
        var imagePath = System.IO.Path.Combine(temp.Path, "image.erofs");
        File.WriteAllBytes(imagePath, new ErofsBuilder().Build(temp.Path, new ErofsOptions()));

        var handler = new ErofsFormatHandler();
        Assert.True(handler.CanHandle(imagePath));

        var result = await handler.ReadMetadataAsync(imagePath, TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Empty(result.Components);
        Assert.Empty(result.Entries);
        Assert.DoesNotContain("FileCount", result.Metadata.Keys);
        Assert.Contains("EROFS", result.Summary);
    }

    [Fact]
    public async Task FormatHandler_PackAndUnpack_RoundTrip()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "packed/file1.txt", "one");
        temp.WriteBytes("packed/sub/file2.bin", [1, 2, 3, 4, 5]);
        var sourceDir = System.IO.Path.Combine(temp.Path, "packed");
        var imagePath = System.IO.Path.Combine(temp.Path, "out.erofs");
        var unpackDir = System.IO.Path.Combine(temp.Path, "unpacked");

        var handler = new ErofsFormatHandler();
        var pack = await handler.PackAsync(sourceDir, imagePath, TestContext.Current.CancellationToken);
        Assert.True(pack.Success);
        Assert.True(File.Exists(imagePath));

        var unpack = await handler.UnpackAsync(imagePath, unpackDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpack.Success);
        Assert.Equal("one", File.ReadAllText(System.IO.Path.Combine(unpackDir, "file1.txt")));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(System.IO.Path.Combine(unpackDir, "sub", "file2.bin")));
    }

    [Fact]
    public void BuildParse_FilePathFactory_ReadsAndExtractsAllFiles()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "file.txt", "stream payload");
        temp.WriteBytes("dir/data.bin", [1, 2, 3]);
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true });
        var imagePath = System.IO.Path.Combine(temp.Path, "image.erofs");
        File.WriteAllBytes(imagePath, image);

        using var parser = new ErofsParser(imagePath);
        var paths = parser.EnumerateFiles()
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "dir/data.bin", "file.txt" }, paths);
        Assert.Equal("stream payload", Encoding.UTF8.GetString(parser.ReadFileBytes("file.txt")));
        Assert.Equal(new byte[] { 1, 2, 3 }, parser.ReadFileBytes("dir/data.bin"));
        Assert.Equal(image.Length, parser.ImageSize);

        var outDir = System.IO.Path.Combine(temp.Path, "out");
        parser.ExtractToDirectory(outDir);
        Assert.Equal("stream payload", File.ReadAllText(System.IO.Path.Combine(outDir, "file.txt")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(System.IO.Path.Combine(outDir, "dir", "data.bin")));
    }

    [Fact]
    public void BuildParse_StreamConstructor_SeekableFileStream_RoundTrips()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "file.txt", "stream payload");
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var imagePath = System.IO.Path.Combine(temp.Path, "image.erofs");
        File.WriteAllBytes(imagePath, image);

        using var fs = File.OpenRead(imagePath);
        using var parser = new ErofsParser(fs);
        Assert.Equal("stream payload", Encoding.UTF8.GetString(parser.ReadFileBytes("file.txt")));
    }

    [Fact]
    public void Parser_RejectsImageTooSmallForSuperblock()
    {
        Assert.Throws<FormatException>(() => new ErofsParser(new byte[100]));
    }

    [Fact]
    public void Parser_RejectsTruncatedImage_MissingMetadata()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "file.txt", "payload");
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions());

        var truncated = image.Take(ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize + 8).ToArray();
        Assert.Throws<FormatException>(() => new ErofsParser(truncated));
    }

    [Fact]
    public void Parser_RejectsTruncatedImage_FileDataOutOfBounds()
    {
        using var temp = new TempDirectory();
        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef", 2000));
        WriteText(temp, "large.txt", payload);
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });

        var truncated = image[..Math.Max(1, image.Length - 20_000)].ToArray();
        using var parser = new ErofsParser(truncated);
        Assert.Throws<FileSystemCorruptedException>(() => parser.ReadFileBytes("large.txt"));
    }

    [Fact]
    public void Parser_TruncatedImage_ExtractThrows_AndLeavesNoPartialFiles()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "small.txt", "payload");
        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef", 2000));
        WriteText(temp, "large.txt", payload);
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var truncated = image[..Math.Max(1, image.Length - 20_000)].ToArray();
        var imagePath = System.IO.Path.Combine(temp.Path, "truncated.erofs");
        File.WriteAllBytes(imagePath, truncated);
        var outDir = System.IO.Path.Combine(temp.Path, "out");

        using var parser = new ErofsParser(imagePath);
        Assert.Throws<FileSystemCorruptedException>(() => parser.ExtractToDirectory(outDir));
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Builder_WritesStandardCompressedIFormat()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 100)));

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true });
        var superblock = ErofsSuperblock.FromImage(image);
        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single();

        var inodeOffset = superblock.GetInodeOffset(entry.Nid);
        var iFormat = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)inodeOffset, 2));
        Assert.Equal(0, iFormat & 0x1);
        Assert.Equal(3, (iFormat >> 1) & 0x7);
        Assert.Equal(ErofsInode.DataLayoutCompactIndexes, (ushort)(iFormat & ErofsInode.DataLayoutMask));
    }

    [Fact]
    public void Builder_WritesStandardFlatIFormat()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", string.Concat(Enumerable.Repeat("0123456789abcdef", 1024)));

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var superblock = ErofsSuperblock.FromImage(image);
        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single();

        var iFormat = BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan((int)superblock.GetInodeOffset(entry.Nid), 2));
        Assert.Equal(ErofsInode.AdviseBitNlinkOneOrDotOmitted, iFormat);
        Assert.Equal(ErofsInode.DataLayoutFlat, (ushort)(iFormat & ErofsInode.DataLayoutMask));
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    public void Builder_SuperblockChecksum_MatchesKernelCsumVerify(int blockSize)
    {
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", "hello");
        var options = new ErofsOptions { Compress = false, BlockSize = blockSize, SuperblockChecksum = true };
        var image = new ErofsBuilder().Build(temp.Path, options);
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.True(superblock.HasCompatChecksum);

        var expected = ErofsCrc32cLe(image.AsSpan(1024 + 8, blockSize - 1024 - 8), ErofsSuperblockChecksumSeed);
        Assert.Equal(expected, superblock.Checksum);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ErofsSuperblock.SuperblockOffset + 4, 4)));

        var tampered = (byte[])image.Clone();
        tampered[1024 + 8] ^= 0x40;
        var altered = ErofsCrc32cLe(tampered.AsSpan(1024 + 8, blockSize - 1024 - 8), ErofsSuperblockChecksumSeed);
        Assert.NotEqual(expected, altered);
    }

    [Fact]
    public void Parser_SuperblockChecksum_ValidImage_ParsesAndTamperedWindow_Throws()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", "hello erofs checksum");
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true });

        using (var parser = new ErofsParser(image))
        {
            Assert.Equal("hello erofs checksum", Encoding.UTF8.GetString(parser.ReadFileBytes("a.txt")));
        }

        var tampered = (byte[])image.Clone();
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.True(superblock.HasCompatChecksum);
        tampered[1024 + ErofsSuperblock.SuperblockSize + 4] ^= 0xFF;
        Assert.Throws<FormatException>(() => new ErofsParser(tampered).Dispose());
    }

    private const uint ErofsSuperblockChecksumSeed = 0x5045B54A;

    private static uint ErofsCrc32cLe(ReadOnlySpan<byte> data, uint seed)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc = ErofsCrc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static readonly uint[] ErofsCrc32cTable = BuildErofsCrc32cTable();

    private static uint[] BuildErofsCrc32cTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78 : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    [Fact]
    public void Parser_UnsupportedDataLayout_ThrowsFileSystemUnsupportedFeatureException()
    {
        const int blockSize = 4096;
        var image = new byte[blockSize * 3];
        new ErofsSuperblock
        {
            MagicValue = ErofsSuperblock.Magic,
            BlockSizeBits = 12,
            RootNid = 0,
            Inos = 2,
            Blocks = 3,
            MetaBlkAddr = 1,
        }.WriteTo(image.AsSpan(ErofsSuperblock.SuperblockOffset));

        new ErofsInode
        {
            Advise = ErofsInode.DataLayoutFlat,
            Mode = ErofsInode.ModeDirectory,
            Size = blockSize,
            U = 2,
            Nlink = 2,
        }.WriteTo(image.AsSpan(blockSize));

        new ErofsInode
        {
            Advise = 0x0A,
            Mode = ErofsInode.ModeRegularFile,
            Size = 1,
        }.WriteTo(image.AsSpan(blockSize + ErofsInode.CompactSize));

        var dirOffset = 2 * blockSize;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(dirOffset), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dirOffset + 8), ErofsDirectoryEntry.DirentSize);
        image[dirOffset + 10] = ErofsInode.FileTypeRegularFile;
        image[dirOffset + ErofsDirectoryEntry.DirentSize] = (byte)'x';

        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single();
        Assert.Equal("x", entry.Path);
        Assert.Throws<FileSystemUnsupportedFeatureException>(() => parser.ReadFileBytes("x"));
    }

    [Fact]
    public async Task FormatHandler_ReadMetadata_TruncatedImage_ReturnsFailure()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "file.txt", "payload");
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions());
        var truncated = image.Take(ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize + 8).ToArray();
        var imagePath = System.IO.Path.Combine(temp.Path, "truncated.erofs");
        File.WriteAllBytes(imagePath, truncated);

        var handler = new ErofsFormatHandler();
        var result = await handler.ReadMetadataAsync(imagePath, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Contains("EROFS", result.Summary);
    }
}