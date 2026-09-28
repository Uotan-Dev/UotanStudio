using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EXT4 htree(dx_dir) 与 metadata_csum inode 校验和的构建测试：
/// 覆盖大目录 htree 索引写入后解析侧线性列出全部条目、以及 inode 尾部 CRC32c 校验和的计算与禁用开关。
/// <para>Builder tests for ext4 htree (dx_dir) indexing and metadata_csum inode checksums: verifies that
/// a large indexed directory still lists every entry through the linear-scan parser, and that the
/// per-inode CRC32c checksums are written (and skipped when disabled) exactly as e2fsprogs computes them.</para>
/// </summary>
public class Ext4HtreeAndCsumTests
{
    private const int BlockSize = 4096;
    private const uint Ext4IndexFlag = 0x0000_1000;
    private const uint FeatureCompatDirIndex = 0x0020;
    private const byte DtDir = 2;

    [Fact]
    public void Builder_SmallDir_ListAllEntries()
    {
        var root = CreateTempDirectory();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(root, $"file{i}.txt"), $"content-{i}");
            }

            var image = Ext4Builder.Build(root);
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));

            var paths = parser.ListFiles()
                .Where(static e => e.Kind != Ext4Parser.Ext4EntryKind.Directory)
                .Select(static e => e.Path)
                .OrderBy(static p => p, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "file0.txt", "file1.txt", "file2.txt", "file3.txt", "file4.txt" }, paths);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Builder_LargeDir_Htree_ListAllEntries()
    {
        var root = CreateTempDirectory();
        try
        {
            for (var i = 0; i < 250; i++)
            {
                var content = new string((char)('a' + (i % 26)), 400);
                File.WriteAllText(Path.Combine(root, $"payload-file-{i:D4}-payload-content.bin"), content);
            }

            var image = Ext4Builder.Build(root);
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));

            var listed = parser.ListFiles()
                .Where(static e => e.Kind != Ext4Parser.Ext4EntryKind.Directory)
                .Select(static e => e.Path)
                .ToArray();
            Assert.Equal(250, listed.Length);
            Assert.Equal(250, listed.Distinct(StringComparer.Ordinal).Count());

            var expected = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < 250; i++)
            {
                expected.Add($"payload-file-{i:D4}-payload-content.bin");
            }

            Assert.Equal(expected, new HashSet<string>(listed, StringComparer.Ordinal));

            Assert.Equal(FeatureCompatDirIndex, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 0x5C, 4)) & FeatureCompatDirIndex);
            var rootInode = InodeOffset(image, 2);
            var rootFlags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rootInode + 32, 4));
            Assert.Equal(Ext4IndexFlag, rootFlags & Ext4IndexFlag);

            var extentStart = ExtentStart(image, rootInode);
            var block0 = checked((int)extentStart) * BlockSize;
            Assert.Equal(12u, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(block0 + 4, 2)));
            Assert.Equal(BlockSize - 12, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(block0 + 12 + 4, 2)));
            Assert.Equal((byte)'.', image[block0 + 8]);
            Assert.Equal((byte)'.', image[block0 + 12 + 8]);
            Assert.Equal((byte)'.', image[block0 + 12 + 9]);
            Assert.Equal(1, image[block0 + 12 + 12 + 4]);

            Assert.Equal(DtDir, image[block0 + 7]);
            Assert.Equal(DtDir, image[block0 + 12 + 7]);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Builder_InodeChecksum_Validates()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "payload.bin"), new string('x', 400));
            var image = Ext4Builder.Build(root);

            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            var inodeNumber = parser.ListFiles().First(static e => e.Path == "payload.bin").Inode;
            var raw = InodeRaw(image, inodeNumber);

            var extraIsize = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(128, 2));
            var hasHigh = extraIsize >= 4;

            var crc = Crc32cReg(ReadUuid(image), 0xFFFF_FFFF);
            Span<byte> le = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(le, inodeNumber);
            crc = Crc32cContinue(crc, le);
            BinaryPrimitives.WriteUInt32LittleEndian(le, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(100, 4)));
            crc = Crc32cContinue(crc, le);

            var copy = raw.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(124, 2), 0);
            if (hasHigh)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(130, 2), 0);
            }

            crc = Crc32cContinue(crc, copy);

            var storedLo = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(124, 2));
            Assert.Equal((ushort)(crc & 0xFFFF), storedLo);
            if (hasHigh)
            {
                var storedHi = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(130, 2));
                Assert.Equal((uint)((storedHi << 16) | storedLo), crc);
            }
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Builder_MetadataCsumDisabled_SkipsInodeChecksum()
    {
        var root = CreateTempDirectory();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                File.WriteAllText(Path.Combine(root, $"a{i}.txt"), new string('q', 400));
            }

            var image = Ext4Builder.Build(root, new Ext4Options { EnableMetadataCsum = false });

            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            var entries = parser.ListFiles()
                .Where(static e => e.Kind != Ext4Parser.Ext4EntryKind.Directory)
                .Select(static e => e.Path)
                .OrderBy(static p => p, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "a0.txt", "a1.txt", "a2.txt" }, entries);

            var inodeNumber = parser.ListFiles().First(static e => e.Path == "a0.txt").Inode;
            var raw = InodeRaw(image, inodeNumber);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(124, 2)));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(130, 2)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 0x64, 4)) & 0x0400);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-ext4-htree-" + Path.GetRandomFileName());
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

    private static uint InodesPerGroup(byte[] image) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 0x28, 4));

    private static int InodeSize(byte[] image) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 0x58, 2));

    private static byte[] ReadUuid(byte[] image) => image[(1024 + 0x68)..(1024 + 0x78)].ToArray();

    /// <summary>计算 inode 在镜像内的文件偏移（组 0）。<para>File offset of an inode within the image (group 0).</para></summary>
    private static int InodeOffset(byte[] image, uint inode)
    {
        var inodesPerGroup = InodesPerGroup(image);
        var inodeTableBlock = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(BlockSize + 8, 4));
        return (int)(inodeTableBlock * BlockSize + ((inode - 1) % inodesPerGroup) * InodeSize(image));
    }

    private static byte[] InodeRaw(byte[] image, uint inode)
    {
        var offset = InodeOffset(image, inode);
        return image[offset..(offset + InodeSize(image))];
    }

    /// <summary>读取 inode i_block 中 depth 0 首 extent 的物理起始块号。<para>Reads the physical start block of the first depth-0 extent in the inode.</para></summary>
    private static long ExtentStart(byte[] image, int inodeOffset)
    {
        var header = inodeOffset + 40;
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(header + 6, 2));
        Assert.Equal(0, depth);
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(header + 12 + 6, 2));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(header + 12 + 8, 4));
        return ((long)startHi << 32) | startLo;
    }

    private static readonly uint[] Crc32cTable = BuildCrc32cTable();

    private static uint[] BuildCrc32cTable()
    {
        const uint poly = 0x82F63B78;
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (uint)i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    private static uint Crc32cReg(ReadOnlySpan<byte> data, uint seed)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint Crc32cContinue(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}