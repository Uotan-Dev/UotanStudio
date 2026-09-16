using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
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
        Assert.Equal("1", result.Metadata["FileCount"]);
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
    public void BuildParse_FilePathFactory_StreamsOnDemand()
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
    public void BuildParse_FilePathFactory_ExtractToDirectory_WritesAllFiles()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "file.txt", "payload");
        temp.WriteBytes("dir/data.bin", [1, 2, 3]);
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true });
        var imagePath = System.IO.Path.Combine(temp.Path, "image.erofs");
        File.WriteAllBytes(imagePath, image);
        var outDir = System.IO.Path.Combine(temp.Path, "out");

        using var parser = new ErofsParser(imagePath);
        parser.ExtractToDirectory(outDir);

        Assert.Equal("payload", File.ReadAllText(System.IO.Path.Combine(outDir, "file.txt")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(System.IO.Path.Combine(outDir, "dir", "data.bin")));
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

        // 只保留超级块及少量后续字节：根 inode / 目录块落在被截断的区域。
        // Keep only the superblock plus a few trailing bytes: the root inode / directory blocks are gone.
        var truncated = image.Take(ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize + 8).ToArray();
        using var parser = new ErofsParser(truncated);
        Assert.Throws<InvalidDataException>(() => parser.EnumerateFiles());
    }

    [Fact]
    public void Parser_RejectsTruncatedImage_FileDataOutOfBounds()
    {
        using var temp = new TempDirectory();
        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef", 2000));
        WriteText(temp, "large.txt", payload);
        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });

        // 裁掉镜像尾部大量文件数据（远超末尾块的填充字节），使文件数据落在镜像边界之外。
        // Truncate a large chunk off the image tail (well past trailing block padding) so the file
        // data falls outside the image bounds.
        var truncated = image[..Math.Max(1, image.Length - 20_000)].ToArray();
        using var parser = new ErofsParser(truncated);
        Assert.Throws<InvalidDataException>(() => parser.ReadFileBytes("large.txt"));
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
        Assert.Throws<InvalidDataException>(() => parser.ExtractToDirectory(outDir));
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Builder_WritesStandardCompressedIFormat()
    {
        // mkfs 对小文件（≤ 一块）一律 tail-packing inline，不会压缩；压缩布局只出现在
        // 真正有收益的可压文件上。此处用一段大文本触发压缩。
        // mkfs never compresses small files (≤ one block) — those are tail-packed inline;
        // compressed layouts only appear for large compressible inputs.
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 100)));

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true });
        var superblock = ErofsSuperblock.FromImage(image);
        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single();

        var inodeOffset = superblock.GetInodeOffset(entry.Nid);
        var iFormat = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)inodeOffset, 2));
        // Kernel erofs_fs.h encoding: bit 0 = version (0 = compact), bits 1..3 = data layout
        // (3 = EROFS_INODE_COMPRESSED_COMPACT), i.e. i_format == 0b0110 for a compact compressed
        // file. Compacted 4B indexes are the erofs-utils default, so the builder emits COMPACT.
        // 内核 erofs_fs.h 编码：位 0 = 版本（0 = compact），位 1..3 = 数据布局
        //（3 = EROFS_INODE_COMPRESSED_COMPACT），即 compact 压缩文件 i_format == 0b0110。
        // compacted 4B 索引是 erofs-utils 默认布局，构建器默认输出 COMPACT。
        Assert.Equal(0, iFormat & 0x1);
        Assert.Equal(3, (iFormat >> 1) & 0x7);
        Assert.Equal(ErofsInode.DataLayoutCompactIndexes, (ushort)(iFormat & ErofsInode.DataLayoutMask));
    }

    [Fact]
    public void Builder_WritesStandardFlatIFormat()
    {
        // mkfs 仅在文件大小恰为整块倍数（无尾部内联）时使用纯 flat 布局；5B 小文件会
        // tail-packing inline。用两整块的不可压数据验证 flat 编码。
        // mkfs uses plain flat only when the file size is an exact multiple of the block size
        // (no inline tail); a 5-byte file would be tail-packed inline. Use two whole blocks of
        // incompressible data to exercise the flat encoding.
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", string.Concat(Enumerable.Repeat("0123456789abcdef", 1024))); // 16384B = 4 整块

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var superblock = ErofsSuperblock.FromImage(image);
        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single();

        var iFormat = BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan((int)superblock.GetInodeOffset(entry.Nid), 2));
        Assert.Equal(0, iFormat);
        Assert.Equal(ErofsInode.DataLayoutFlat, (ushort)(iFormat & ErofsInode.DataLayoutMask));
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    public void Builder_SuperblockChecksum_MatchesKernelCsumVerify(int blockSize)
    {
        // Linux fs/erofs/super.c erofs_superblock_csum_verify computes
        // crc32c(0x5045B54A) over bytes [1024 + 8, blockSize) i.e. skipping the magic
        // and the checksum field. Rebuild the image deterministically and verify the
        // written checksum against that formula.
        // 内核 erofs_superblock_csum_verify 用 crc32c(0x5045B54A) 覆盖 [1024 + 8, blockSize)（跳过
        // 魔数和校验和字段）。此处按同一公式独立重算并断言与写盘值一致。
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", "hello");
        var options = new ErofsOptions { Compress = false, BlockSize = blockSize, SuperblockChecksum = true };
        var image = new ErofsBuilder().Build(temp.Path, options);
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.True(superblock.HasCompatChecksum);

        var expected = ErofsCrc32cLe(image.AsSpan(1024 + 8, blockSize - 1024 - 8), ErofsSuperblockChecksumSeed);
        Assert.Equal(expected, superblock.Checksum);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ErofsSuperblock.SuperblockOffset + 4, 4)));

        // The checksum covers the hashed region: flipping a byte there changes the value.
        // 校验和覆盖该区域：篡改其中任一字节会使重算值变化。
        var tampered = (byte[])image.Clone();
        tampered[1024 + 8] ^= 0x40;
        var altered = ErofsCrc32cLe(tampered.AsSpan(1024 + 8, blockSize - 1024 - 8), ErofsSuperblockChecksumSeed);
        Assert.NotEqual(expected, altered);
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
    public void Parser_UnsupportedDataLayout_ThrowsInvalidDataException()
    {
        // Hand-crafted minimal image: a flat root directory plus one file inode using a reserved
        // (unsupported by the parser) data layout. The parser must fail loudly instead of reading
        // garbage.
        // 手工构造的最小镜像：扁平根目录 + 使用保留（解析器不支持）布局的文件 inode。
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

        // inode 表位于 meta_blkaddr(块 1) 起点：root(0) 与文件(1) 各占 32B 槽。
        // The inode table starts at meta_blkaddr (block 1): root(0) and the file(1) each take a
        // 32-byte slot.
        // Root directory inode (compact, flat) at metadata offset 0; directory data at block 2.
        new ErofsInode
        {
            Advise = ErofsInode.DataLayoutFlat,
            Mode = ErofsInode.ModeDirectory,
            Size = blockSize,
            U = 2,
            Nlink = 2,
        }.WriteTo(image.AsSpan(blockSize));

        // File inode (compact, reserved layout 0x0A = unsupported) at metadata offset 32 (nid 1).
        new ErofsInode
        {
            Advise = 0x0A, // 未定义的 data layout（位 1..3 = 101）
            Mode = ErofsInode.ModeRegularFile,
            Size = 1,
        }.WriteTo(image.AsSpan(blockSize + ErofsInode.CompactSize));

        // Directory block at block 2: dirent 数组在前、名字区紧随（de[0].nameoff = 名字区起点）。
        // Directory block at block 2: dirent array first, then the name area (de[0].nameoff
        // anchors the name area).
        var dirOffset = 2 * blockSize;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(dirOffset), 1); // nid 1 = 文件
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dirOffset + 8), ErofsDirectoryEntry.DirentSize);
        image[dirOffset + 10] = ErofsInode.FileTypeRegularFile;
        image[dirOffset + ErofsDirectoryEntry.DirentSize] = (byte)'x';

        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single();
        Assert.Equal("x", entry.Path);
        Assert.Throws<InvalidDataException>(() => parser.ReadFileBytes("x"));
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