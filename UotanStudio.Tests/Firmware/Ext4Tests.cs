using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EXT4 builder → parser round-trip tests: builds an image from a temporary directory
/// containing files, a sub-directory, a symlink and a large file, then reads it back
/// and asserts the file tree, file contents and sparse-hole zero-filling.
/// <para>EXT4 构建器 → 解析器往返测试：从含文件、子目录、符号链接与大文件的临时目录构建镜像，
/// 再读回并断言文件树、文件内容与稀疏洞补零。</para>
/// </summary>
public class Ext4Tests
{
    private const int BlockSize = 4096;

    private static readonly byte[] BinaryBlob = [0x00, 0x01, 0x02, 0xFE, 0xFF, 0x80, 0x7F, 0x10, 0x20, 0x30];

    [Fact]
    public void BuildAndParse_RoundTripsFileTreeContentsAndSymlink()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), BinaryBlob);
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "inner.txt"), "inner content\n");
            var hasSymlink = TryCreateSymlink(Path.Combine(root, "link"), "hello.txt");

            var largeBytes = new byte[3 * BlockSize + 123];
            for (var i = 0; i < largeBytes.Length; i++)
            {
                largeBytes[i] = (byte)(i * 31 + 7);
            }

            File.WriteAllBytes(Path.Combine(root, "large.bin"), largeBytes);

            var image = Ext4Builder.Build(root);
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));

            var expected = new SortedSet<string> { "hello.txt", "blob.bin", "subdir", "subdir/inner.txt", "large.bin" };
            if (hasSymlink)
            {
                expected.Add("link");
            }

            var actual = new SortedSet<string>(parser.ListFiles().Select(static e => e.Path));
            Assert.Equal(expected, actual);

            var entries = parser.ListFiles().ToDictionary(static e => e.Path, StringComparer.Ordinal);
            Assert.Equal(Encoding.UTF8.GetBytes("hello ext4\n"), ReadFile(parser, "hello.txt"));
            Assert.Equal(BinaryBlob, ReadFile(parser, "blob.bin"));
            Assert.Equal(Encoding.UTF8.GetBytes("inner content\n"), ReadFile(parser, "subdir/inner.txt"));
            Assert.Equal(largeBytes, ReadFile(parser, "large.bin"));

            var subdir = entries["subdir"];
            Assert.Equal(Ext4Parser.Ext4EntryKind.Directory, subdir.Kind);
            Assert.True(subdir.Size > 0);

            if (hasSymlink)
            {
                var link = entries["link"];
                Assert.Equal(Ext4Parser.Ext4EntryKind.Symlink, link.Kind);
                Assert.Equal("hello.txt", link.SymlinkTarget);
                Assert.Equal(Encoding.UTF8.GetBytes("hello.txt"), ReadFile(parser, "link"));
            }
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void BuildWithSparse_ReadsHolesBackAsZeros()
    {
        var root = CreateTempDirectory();
        try
        {
            var content = new byte[BlockSize * 3];
            Encoding.ASCII.GetBytes("sparse head\n", content);
            Encoding.ASCII.GetBytes("sparse tail\n", content.AsSpan(BlockSize * 2));
            File.WriteAllBytes(Path.Combine(root, "sparse.bin"), content);

            var image = Ext4Builder.Build(root, new Ext4Options { EnableSparse = true });
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));

            var readBack = ReadFile(parser, "sparse.bin");
            Assert.Equal(content.Length, readBack.Length);
            Assert.Equal(content, readBack);

            // The all-zero middle block must come back as zeros.
            // 全零中间块读回必须为零。
            for (var i = BlockSize; i < BlockSize * 2; i++)
            {
                Assert.Equal(0, readBack[i]);
            }
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public async Task Handler_DetectsMetadataAndUnpacksBuiltImage()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "b.txt"), "beta");

            var imagePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".img");
            try
            {
                File.WriteAllBytes(imagePath, Ext4Builder.Build(root));
                var handler = new Ext4FormatHandler();
                Assert.True(handler.CanHandle(imagePath));
                Assert.Equal("EXT4", handler.FormatName);
                Assert.Contains("img", handler.FileExtensions);

                var metadata = await handler.ReadMetadataAsync(imagePath, TestContext.Current.CancellationToken);
                Assert.True(metadata.Success);
                Assert.Equal(BlockSize.ToString(), metadata.Metadata["BlockSize"]);
                Assert.True(long.Parse(metadata.Metadata["PartitionSize"]) > 0);
                Assert.True(int.Parse(metadata.Metadata["FileCount"]) >= 2);
                Assert.Contains("EXT4", metadata.Summary);

                var unpackDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                var unpackResult = await handler.UnpackAsync(imagePath, unpackDirectory, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(unpackResult.Success);
                Assert.Equal("alpha", File.ReadAllText(Path.Combine(unpackDirectory, "a.txt")));
                Assert.Equal("beta", File.ReadAllText(Path.Combine(unpackDirectory, "nested", "b.txt")));
                DeleteRecursive(unpackDirectory);
            }
            finally
            {
                File.Delete(imagePath);
            }
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Builder_WritesGroupDescriptorAtStandardBlock1Location()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "beta");

            var image = Ext4Builder.Build(root);

            // Superblock at offset 1024: 4 KiB blocks, s_first_data_block == 0.
            // 偏移 1024 的超级块：4 KiB 块，s_first_data_block == 0。
            Assert.Equal(0xEF53, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 56, 2)));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 24, 4))); // s_log_block_size

            // The primary GDT lives in the block right after the superblock block
            // ((s_first_data_block + 1) * block_size = block 1 = byte offset 4096). With the
            // reserved GDT (16 blocks for a small fs, matching mke2fs's 1024x-growth reservation)
            // the block bitmap moves to block 1 + 1 + 16 = 18.
            // 主组描述符表位于超级块块之后的块（(s_first_data_block + 1) * 块大小 = 块 1 = 偏移 4096）。
            // 启用保留 GDT 后（小文件系统为 16 块，对齐 mke2fs 的 1024 倍增长预留），
            // 块位图移至块 1 + 1 + 16 = 18。
            var gdtOffset = BlockSize;
            Assert.Equal(18u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(gdtOffset, 4)));        // bg_block_bitmap_lo
            Assert.Equal(19u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(gdtOffset + 4, 4)));    // bg_inode_bitmap_lo
            Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(gdtOffset + 8, 4)));    // bg_inode_table_lo

            // Offset 2048 is the tail of block 0 (boot + superblock) and must not hold the GDT.
            // 偏移 2048 位于块 0 尾部（引导 + 超级块），不应是 GDT。
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(2048, 4)));

            // The parser must locate the GDT at the same standard offset and round-trip the tree.
            // 解析器必须在同一标准偏移定位 GDT 并正确往返目录树。
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            var paths = parser.ListFiles().Select(static e => e.Path).OrderBy(static p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "a.txt", "sub", "sub/b.txt" }, paths);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Parser_TruncatedImage_ThrowsInvalidDataException()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "beta");
            var largeBytes = new byte[BlockSize * 8];
            for (var i = 0; i < largeBytes.Length; i++)
            {
                largeBytes[i] = (byte)(i * 13 + 1);
            }

            File.WriteAllBytes(Path.Combine(root, "large.bin"), largeBytes);
            var image = Ext4Builder.Build(root);

            // Truncate to the end of the metadata region so every data block (directory blocks
            // included) is gone while the superblock, GDT and inode table remain intact.
            // 截断到元数据区末尾：所有数据块（含目录块）消失，而超级块、GDT 与 inode 表仍完整。
            var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 40, 4));
            var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 88, 2));
            inodeSize = inodeSize == 0 ? (ushort)256 : inodeSize;
            var inodesPerBlock = BlockSize / inodeSize;
            var inodeTableBlocks = (inodesPerGroup + inodesPerBlock - 1) / inodesPerBlock;
            var metadataBytes = (4 + (int)inodeTableBlocks) * BlockSize;
            Assert.InRange(metadataBytes, 1, image.Length);
            var truncated = image[..metadataBytes].ToArray();

            // A truncated image must fail with a bounded format error, never with an
            // out-of-range stream exception or an infinite loop.
            // 截断镜像必须抛出有界格式错误，而非越界流异常或死循环。
            Assert.Throws<InvalidDataException>(() =>
            {
                using var parser = new Ext4Parser(new MemoryStream(truncated, writable: false));
                _ = parser.ListFiles();
            });
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Parser_DirectoryCycle_ThrowsInvalidDataExceptionInsteadOfHanging()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "inner.txt"), "inner");
            File.WriteAllText(Path.Combine(root, "top.txt"), "top");

            var image = Ext4Builder.Build(root);

            // Locate the root directory (inode 2) data block via the GDT inode table and its extent.
            // 通过 GDT 的 inode 表与 extent 定位根目录（inode 2）的数据块。
            var inodeTableOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(BlockSize + 8, 4)) * BlockSize);
            var rootInodeOffset = inodeTableOffset + (2 - 1) * 256;
            Assert.Equal(0xF30A, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(rootInodeOffset + 40, 2))); // extent magic
            var rootDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rootInodeOffset + 40 + 20, 4)); // ee_start_lo
            var rootDirOffset = checked((int)rootDataBlock * BlockSize);

            var patched = false;
            var offset = 0;
            while (offset + 8 <= BlockSize)
            {
                var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(rootDirOffset + offset + 4, 2));
                if (recordLength < 8)
                {
                    break;
                }

                var nameLength = image[rootDirOffset + offset + 6];
                var name = Encoding.UTF8.GetString(image, rootDirOffset + offset + 8, nameLength);
                if (name == "subdir")
                {
                    // Point the directory entry back at the root inode to form a cycle.
                    // 将该目录项指向根 inode 以形成环。
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rootDirOffset + offset, 4), 2);
                    patched = true;
                    break;
                }

                offset += recordLength;
            }

            Assert.True(patched, "Test setup failed: 'subdir' directory entry not found.");

            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            Assert.Throws<InvalidDataException>(() => parser.ListFiles());
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Build_TinyPartition_YieldsSmallImageWithSaneBlockGroupSize()
    {
        var root = CreateTempDirectory();
        try
        {
            // A sub-1 MiB source must not be inflated to a full 128 MiB single block group;
            // the builder shrinks blocks_per_group and the journal for tiny content.
            // 不足 1 MiB 的源目录不得膨胀为单个 128 MiB 块组；构建器应为小内容缩小块组与日志。
            var payload = new byte[512 * 1024];
            RandomNumberGenerator.Fill(payload);
            File.WriteAllBytes(Path.Combine(root, "payload.bin"), payload);
            File.WriteAllText(Path.Combine(root, "meta.txt"), "vendor partition");

            var image = Ext4Builder.Build(root);

            // The block group must be shrunk so the output no longer hits the former
            // 8 × blockSize × blockSize (128 MiB) floor. Semantics stay intact: the
            // filesystem must still parse and read the payload back byte-for-byte.
            // 块组应被缩小，使小内容不再命中原先 8 × blockSize × blockSize（128 MiB）的下限；
            // 语义保持完整：文件系统仍需可解析，并能逐字节读回数据。
            Assert.True(image.LongLength < 16L * 1024 * 1024, $"image too large: {image.LongLength}");
            var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 32, 4));
            var totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 4, 4));
            Assert.InRange(blocksPerGroup, 8u, 32768u);
            Assert.Equal((uint)(image.Length / 4096), totalBlocks);

            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            Assert.Contains(parser.ListFiles(), static e => e.Path == "payload.bin");
            using var sink = new MemoryStream();
            parser.ReadFile("payload.bin", sink);
            Assert.Equal(payload, sink.ToArray());
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-ext4-" + Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static bool TryCreateSymlink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch
        {
            // Symlink creation may require developer mode / elevation on Windows.
            // Windows 上创建符号链接可能需要开发者模式或管理员权限。
            return false;
        }
    }

    private static byte[] ReadFile(Ext4Parser parser, string path)
    {
        using var ms = new MemoryStream();
        parser.ReadFile(path, ms);
        return ms.ToArray();
    }
}