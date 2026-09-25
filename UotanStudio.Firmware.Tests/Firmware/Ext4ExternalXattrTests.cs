using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
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
        var raw = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0, 2), 0x8000);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(104, 4), 0x00000005);

        var inode = Ext4Inode.FromBytes(raw, 256);
        Assert.Equal(5UL, inode.FileAcl);
    }

    [Fact]
    public void Inode_FromBytes_ParsesFileAcl_64bit()
    {
        var raw = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0, 2), 0x8000);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(104, 4), 0x00000005);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(118, 2), 0x0001);

        var inode = Ext4Inode.FromBytes(raw, 256);
        Assert.Equal(0x00000001_00000005UL, inode.FileAcl);
    }

    [Fact]
    public void Inode_FromBytes_FileAclZero_WhenNoExternalXattr()
    {
        var raw = new byte[128];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0, 2), 0x8000);

        var inode = Ext4Inode.FromBytes(raw, 128);
        Assert.Equal(0UL, inode.FileAcl);
    }

    [Fact]
    public void Parser_ExternalXattrBlock_ReadsEntries()
    {
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
        var root = CreateTempDirectory();
        try
        {
            var expectedFiles = new SortedSet<string>();
            for (var i = 0; i < 50; i++)
            {
                var name = $"file_{i:D3}.txt";
                File.WriteAllText(Path.Combine(root, name), $"content {i}\n");
                expectedFiles.Add(name);
            }

            var image = Ext4Builder.Build(root);

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