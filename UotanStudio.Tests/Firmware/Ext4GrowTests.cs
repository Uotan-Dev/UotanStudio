using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using Xunit;
using static UotanStudio.Tests.Firmware.WslTestHelpers;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EXT4 grow tests: enlarge a built image by whole block groups and prove the result is both
/// self-consistent (Ext4Parser) and accepted by the authoritative external checker (e2fsck),
/// including growth across multiple sparse-super backup boundaries and allocation into the
/// freshly appended capacity.
/// <para>EXT4 扩容测试：以整块块组扩大构建后的镜像，并证明结果既自洽（Ext4Parser）又被权威外部工具
/// e2fsck 接受，包含跨越多个 sparse-super 备份边界以及向新追加容量写入的场景。</para>
/// </summary>
public sealed class Ext4GrowTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Grow_ToLargerTarget_AppendsWholeGroups_AndPreservesFiles()
    {
        var (root, dir) = CreateSource();
        try
        {
            var image = Path.Combine(root, "fs.img");
            Ext4Builder.Build(dir, image);

            ulong beforeBlocks;
            byte[] innerBefore;
            using (var pre = new Ext4Parser(image))
            {
                beforeBlocks = pre.BlockCount;
                innerBefore = ReadFileBytes(pre, "subdir/inner.txt");
            }

            Ext4Builder.Grow(image, (long)beforeBlocks + 500);

            using var post = new Ext4Parser(image);
            Assert.True(post.BlockCount > beforeBlocks, "block count must grow");
            Assert.Equal(post.PartitionSize, new FileInfo(image).Length);
            Assert.Equal(innerBefore, ReadFileBytes(post, "subdir/inner.txt"));
            Assert.Equal(Encoding.UTF8.GetBytes("hello ext4 grow\n"), ReadFileBytes(post, "hello.txt"));
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Grow_AlsoIncreasesInodeCapacity()
    {
        var (root, dir) = CreateSource();
        try
        {
            var image = Path.Combine(root, "fs.img");
            Ext4Builder.Build(dir, image);

            uint beforeInodes;
            using (var pre = new Ext4Parser(image)) { beforeInodes = pre.InodeCount; }

            Ext4Builder.Grow(image, 10_000);

            using var post = new Ext4Parser(image);
            Assert.True(post.InodeCount > beforeInodes, "each appended group carries its inode table");
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Grow_TargetAtOrBelowCurrent_IsExactNoOp()
    {
        var (root, dir) = CreateSource();
        try
        {
            var image = Path.Combine(root, "fs.img");
            Ext4Builder.Build(dir, image);

            ulong beforeBlocks;
            long beforeSize;
            using (var pre = new Ext4Parser(image)) { beforeBlocks = pre.BlockCount; beforeSize = pre.PartitionSize; }

            Ext4Builder.Grow(image, (long)beforeBlocks);

            using var post = new Ext4Parser(image);
            Assert.Equal(beforeBlocks, post.BlockCount);
            Assert.Equal(beforeSize, post.PartitionSize);
            Assert.Equal(beforeSize, new FileInfo(image).Length);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Grow_BeyondPrimaryGdtCapacity_ThrowsExplicitly()
    {
        var (root, dir) = CreateSource();
        try
        {
            var image = Path.Combine(root, "fs.img");
            Ext4Builder.Build(dir, image);

            // A target this large would require the primary group descriptor table to span more
            // blocks than the reserved-GDT layout can absorb; Grow must refuse rather than corrupt.
            // <para>如此巨大的目标值会要求主块组描述符表跨越超出保留 GDT 布局所能吸收的块数；Grow 必须拒绝而非破坏镜像。</para>
            var ex = Assert.Throws<NotSupportedException>(() => Ext4Builder.Grow(image, 400_000_000));
            Assert.Contains("GDT", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Grow_E2fsck_AcceptsGrownImage(bool metadataCsum, bool includeJournal)
    {
        if (!IsWslAvailable() || !IsToolAvailable("e2fsck"))
        {
            Assert.Skip("WSL or e2fsck is unavailable; structural grow validation skipped.");
        }

        var (root, dir) = CreateSource();
        try
        {
            var image = Path.Combine(root, "fs.img");
            Ext4Builder.Build(dir, image, new Ext4Options { EnableMetadataCsum = metadataCsum, IncludeJournal = includeJournal });

            ulong beforeBlocks;
            using (var pre = new Ext4Parser(image)) { beforeBlocks = pre.BlockCount; }
            Ext4Builder.Grow(image, (long)beforeBlocks * 4);

            var (output, exit) = RunWsl($"e2fsck -fn {Quote(ToWslPath(image))}");
            Assert.True(exit == 0, $"e2fsck rejected the grown image (exit={exit}):\n{output}");
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Grow_AcrossMultipleBackupGroups_E2fsckClean_AndAllocatable()
    {
        if (!IsWslAvailable() || !IsToolAvailable("e2fsck") || !IsToolAvailable("debugfs"))
        {
            Assert.Skip("WSL, e2fsck or debugfs is unavailable; backup-group grow validation skipped.");
        }

        var (root, dir) = CreateSource();
        try
        {
            // Grow to a target that introduces several new sparse-super backup groups, forcing the
            // resize inode and every backup superblock/GDT copy to be rebuilt.
            // <para>扩大到会引入多个新的 sparse-super 备份组的规模，迫使 resize inode 与所有备份超级块/GDT 副本重建。</para>
            var image = Path.Combine(root, "fs.img");
            Ext4Builder.Build(dir, image);
            ulong beforeBlocks;
            using (var pre = new Ext4Parser(image)) { beforeBlocks = pre.BlockCount; }
            Ext4Builder.Grow(image, (long)beforeBlocks * 6);

            var wsl = ToWslPath(image);
            var (ckOut, ckExit) = RunWsl($"e2fsck -fn {Quote(wsl)}");
            Assert.True(ckExit == 0, $"e2fsck rejected the multi-backup grown image (exit={ckExit}):\n{ckOut}");

            // debugfs allocates through the grown bitmaps; a fresh inode + a re-check prove the new
            // block groups are genuinely usable, not merely present.
            // <para>debugfs 通过扩容后的位图分配；新 inode 加上复检证明新块组真正可用，而非仅存在。</para>
            var (_, fillExit) = RunWsl($"head -c 200000 /dev/urandom > /tmp/growblob && debugfs -w -R 'write /tmp/growblob /grown.bin' {Quote(wsl)}");
            Assert.Equal(0, fillExit);
            var (ck2Out, ck2Exit) = RunWsl($"e2fsck -fn {Quote(wsl)}");
            Assert.True(ck2Exit == 0, $"e2fsck rejected the grown image after allocation (exit={ck2Exit}):\n{ck2Out}");
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    private static (string Root, string Dir) CreateSource()
    {
        var root = CreateTempDirectory();
        var dir = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello ext4 grow\n");
        File.WriteAllText(Path.Combine(dir, "subdir", "inner.txt"), new string('q', 3 * BlockSize + 40));
        return (root, dir);
    }

    private static byte[] ReadFileBytes(Ext4Parser parser, string path)
    {
        using var ms = new MemoryStream();
        parser.ReadFile(path, ms);
        return ms.ToArray();
    }
}