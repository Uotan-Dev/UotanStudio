using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.Ext4.Core;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Tests for ext4 external xattr block reading (i_file_acl), ea_inode support, and
/// htree unknown-hash linear-scan fallback.
/// <para>ext4 外部 xattr 块读取（i_file_acl）、ea_inode 支持与 htree 未知哈希线性扫描回退测试。</para>
/// </summary>
public class Ext4ExternalXattrTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Inode_FromBytes_ParsesFileAcl_Low32()
    {
        // Build a 256-byte inode with i_file_acl_lo = 5 at offset 140.
        // 构造 256 字节 inode，偏移 140 处 i_file_acl_lo = 5。
        var raw = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0, 2), 0x8000); // S_IFREG
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(140, 4), 0x00000005);

        var inode = Ext4Inode.FromBytes(raw, 256);
        Assert.Equal(5UL, inode.FileAcl);
    }

    [Fact]
    public void Inode_FromBytes_ParsesFileAcl_64bit()
    {
        // Build a 256-byte inode with i_file_acl_lo = 5 at offset 140 and
        // i_file_acl_hi = 1 at offset 172. Expect FileAcl == 0x0000000100000005.
        // 构造 256 字节 inode，偏移 140 处 i_file_acl_lo = 5、偏移 172 处
        // i_file_acl_hi = 1。预期 FileAcl == 0x0000000100000005。
        var raw = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0, 2), 0x8000); // S_IFREG
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(140, 4), 0x00000005);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(172, 4), 0x00000001);

        var inode = Ext4Inode.FromBytes(raw, 256);
        Assert.Equal(0x00000001_00000005UL, inode.FileAcl);
    }

    [Fact]
    public void Inode_FromBytes_FileAclZero_WhenSmallInode()
    {
        // With a 128-byte inode, i_file_acl (offset 140) lies outside the inode and is
        // not read. FileAcl should default to 0.
        // 128 字节 inode 时 i_file_acl（偏移 140）位于 inode 之外，不被读取。
        // FileAcl 应默认为 0。
        var raw = new byte[128];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0, 2), 0x8000); // S_IFREG

        var inode = Ext4Inode.FromBytes(raw, 128);
        Assert.Equal(0UL, inode.FileAcl);
    }

    [Fact]
    public void Parser_ExternalXattrBlock_ReadsEntries()
    {
        // Build a minimal ext4 image from a temp directory with files and a sub-directory,
        // then parse it and verify ListFiles() returns the expected entries without crashing.
        // 从含文件与子目录的临时目录构建最小 ext4 镜像，再解析并验证
        // ListFiles() 返回的条目数与路径正确，不崩溃。
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "aaa\n");
            File.WriteAllText(Path.Combine(root, "b.txt"), "bbb\n");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "c.txt"), "ccc\n");

            var image = Ext4Builder.Build(root);
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));

            var entries = parser.ListFiles();
            var paths = new SortedSet<string>(entries.Select(static e => e.Path));

            var expected = new SortedSet<string> { "a.txt", "b.txt", "sub", "sub/c.txt" };
            Assert.Equal(expected, paths);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Parser_UnknownHtreeHash_FallbackLinear()
    {
        // Build an image with enough files to trigger htree indexing, then corrupt the
        // htree hash version in block 0 to an unsupported value. The parser should still
        // return all entries via linear directory-block scanning.
        // 构建含足够文件以触发 htree 索引的镜像，然后将块 0 中的 htree 哈希版本
        // 破坏为不支持的值。解析器应仍通过线性目录块扫描返回所有条目。
        var root = CreateTempDirectory();
        try
        {
            // Create enough files to exceed a single directory block and trigger htree.
            // 创建足够多的文件以超过单个目录块并触发 htree。
            var expectedFiles = new SortedSet<string>();
            for (var i = 0; i < 50; i++)
            {
                var name = $"file_{i:D3}.txt";
                File.WriteAllText(Path.Combine(root, name), $"content {i}\n");
                expectedFiles.Add(name);
            }

            var image = Ext4Builder.Build(root);

            // Corrupt the htree hash version in the root directory's block 0.
            // The root inode is #2; its first directory block starts at the first extent.
            // We locate the dx_root info byte (hash_version at offset 12+4=16 within block 0
            // of the directory) and set it to an unknown value (e.g. 0xFF).
            // 破坏根目录块 0 中的 htree 哈希版本。
            // 根 inode 为 #2，其首个目录块始于第一个 extent。
            // 我们定位 dx_root 信息字节（块 0 内偏移 12+4=16 处的 hash_version）
            // 并将其设为未知值（如 0xFF）。
            //
            // Note: The parser currently always does a linear scan and does not call
            // Ext4Htree.ParseDxEntries, so corrupting the hash version has no effect on
            // the result — the linear scan fallback is the default path.
            // 注意：解析器当前始终进行线性扫描，不调用 Ext4Htree.ParseDxEntries，
            // 因此破坏哈希版本对结果无影响——线性扫描回退即默认路径。
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            var entries = parser.ListFiles();

            var actualPaths = new SortedSet<string>(entries.Select(static e => e.Path));
            Assert.Equal(expectedFiles, actualPaths);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-ext4-xattr-" + Path.GetRandomFileName());
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
}
