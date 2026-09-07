using System.Security.Cryptography;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// WSL2 Alpine 工具级交叉验证：构建 EXT4/EROFS/F2FS 镜像后在 Alpine WSL2 中以 e2fsck、
/// fsck.erofs、fsck.f2fs 等原生工具进行只读检查，并以 debugfs/dump.erofs/dump.f2fs
/// 校验文件列表与内容。
/// <para>WSL2 Alpine tool-level cross-validation: builds EXT4/EROFS/F2FS images, then
/// runs read-only checks with e2fsck, fsck.erofs, fsck.f2fs and verifies file listings
/// and contents with debugfs/dump.erofs/dump.f2fs inside Alpine WSL2.</para>
/// </summary>
/// <remarks>
/// Uses existing tools in the selected WSL distribution; missing tools skip the affected cases.
/// <para>使用所选 WSL 发行版中已有的工具；缺少依赖时跳过，不安装软件或加载宿主内核模块。</para>
/// </remarks>
public class WslCrossValidationTests
{
    private static void AssertF2fsClean(string output, int exitCode)
    {
        Assert.True(exitCode == 0 && !output.Contains("[ASSERT]", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("[Fail]", StringComparison.OrdinalIgnoreCase),
            $"fsck.f2fs failed (exit={exitCode}):\n{output}");
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
            return false;
        }
    }

    [Fact]
    public void Ext4_E2fsck_PassesOnBuiltImage()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("e2fsck") || !WslTestHelpers.IsToolAvailable("debugfs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), [0x00, 0x01, 0x02, 0xFF]);
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "nested.txt"), "nested content");
            TryCreateSymlink(Path.Combine(root, "link"), "hello.txt");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath, new Ext4Options { EnableSparse = true });
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                var (output, exitCode) = WslTestHelpers.RunWsl($"e2fsck -n -f {wslPath}");
                Assert.True(exitCode <= 1, $"e2fsck failed (exit={exitCode}):\n{output}");
                // e2fsck -n 对可修复问题返回 0，需显式断言关键错误不存在。
                // e2fsck -n exits 0 for fixable problems, so the key errors must be asserted absent.
                Assert.DoesNotContain("not valid", output);
                Assert.DoesNotContain("Free blocks count wrong", output);
                Assert.DoesNotContain("Free inodes count wrong", output);

                var (lsOutput, lsExit) = WslTestHelpers.RunWsl($"debugfs -R 'ls /' {wslPath}");
                Assert.True(lsExit == 0, $"debugfs ls failed:\n{lsOutput}");
                Assert.Contains("hello.txt", lsOutput);
                Assert.Contains("blob.bin", lsOutput);
                Assert.Contains("subdir", lsOutput);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void Ext4_Dumpe2fs_ShowsCorrectSuperblockFields()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("dumpe2fs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "test.txt"), "test");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-dump-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath);
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                var (output, exitCode) = WslTestHelpers.RunWsl($"dumpe2fs -h {wslPath}");
                Assert.True(exitCode == 0, $"dumpe2fs failed:\n{output}");
                Assert.Contains("0xEF53", output);
                Assert.Contains("Filesystem features:", output);
                // 特性集须与 mke2fs -t ext4 默认一致（兼容/不兼容/ro 兼容三组）。
                // The feature set must match the mke2fs -t ext4 defaults (all three flag groups).
                Assert.Contains("has_journal", output);
                Assert.Contains("ext_attr", output);
                Assert.Contains("resize_inode", output);
                Assert.Contains("dir_index", output);
                Assert.Contains("filetype", output);
                Assert.Contains("extent", output);
                Assert.Contains("64bit", output);
                Assert.Contains("flex_bg", output);
                Assert.Contains("sparse_super", output);
                Assert.Contains("large_file", output);
                Assert.Contains("huge_file", output);
                Assert.Contains("dir_nlink", output);
                Assert.Contains("extra_isize", output);
                Assert.Contains("metadata_csum", output);
                // 不允许误置位（此前曾把 BIGALLOC/READONLY 位错写入 ro_compat）。
                // Must not misdeclare (BIGALLOC/READONLY were once wrongly set in ro_compat).
                Assert.DoesNotContain("bigalloc", output);
                Assert.Contains("Reserved GDT blocks", output);
                Assert.DoesNotContain("Reserved GDT blocks:      0", output);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void Ext4_Resize2fsOnlineGrow_Passes()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("resize2fs") || !WslTestHelpers.IsToolAvailable("e2fsck")) { Assert.Skip("resize2fs or e2fsck is unavailable"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "resize me");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-resize-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath);
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                // 真实在线扩容（128M → 256M）后重新 e2fsck：彻底校验 resize_inode / 保留 GDT
                // 布局可支撑在线扩容。
                // A real online resize (128M → 256M) followed by e2fsck: fully validates that the
                // resize inode / reserved GDT layout supports online growth.
                var (output, exitCode) = WslTestHelpers.RunWsl($"resize2fs {wslPath} 256M");
                Assert.True(exitCode == 0, $"resize2fs failed (exit={exitCode}):\n{output}");

                var (fsckOutput, fsckExit) = WslTestHelpers.RunWsl($"e2fsck -n -f {wslPath}");
                Assert.True(fsckExit <= 1, $"post-resize e2fsck failed (exit={fsckExit}):\n{fsckOutput}");
                Assert.DoesNotContain("not valid", fsckOutput);
                Assert.DoesNotContain("Free blocks count wrong", fsckOutput);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void Erofs_FsckErofs_PassesOnBuiltImage()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("fsck.erofs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello erofs\n");
            File.WriteAllBytes(Path.Combine(root, "data.bin"), [0xAA, 0xBB, 0xCC]);
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            File.WriteAllText(Path.Combine(root, "dir", "file.txt"), "inside dir");

            var builder = new ErofsBuilder();
            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-erofs-{Path.GetRandomFileName()}.img");
            try
            {
                builder.Build(root, imagePath);
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                var (output, exitCode) = WslTestHelpers.RunWsl($"fsck.erofs {wslPath}");
                Assert.True(exitCode <= 1, $"fsck.erofs failed (exit={exitCode}):\n{output}");
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void Erofs_FsckErofs_LzmaBigPclusterCompressed_Passes()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("fsck.erofs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            // 高可压数据 → 多 pcluster（每 pcluster 覆盖多个逻辑簇），走 LZMA big-pcluster
            // 索引路径（h_advise BIG_PCLUSTER_1|2 + 首个 NONHEAD CBLKCNT + compact 4B 索引）。
            // Highly compressible data → multiple pclusters (each covering several logical
            // clusters), exercising the LZMA big-pcluster index path (h_advise BIG_PCLUSTER_1|2 +
            // first-NONHEAD CBLKCNT + compact 4B indexes).
            var pattern = new string('A', 64) + "abcdefghijklmnopqrstuvwxyz0123456789";
            File.WriteAllText(Path.Combine(root, "compressible.txt"), string.Concat(Enumerable.Repeat(pattern, 600)));
            File.WriteAllBytes(Path.Combine(root, "random.bin"), RandomNumberGenerator.GetBytes(96 * 1024));

            var builder = new ErofsBuilder();
            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-erofs-lzma-{Path.GetRandomFileName()}.img");
            try
            {
                builder.Build(root, imagePath, new ErofsOptions { Algorithm = ErofsCompressionAlgorithm.Lzma });
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                var (output, exitCode) = WslTestHelpers.RunWsl($"fsck.erofs {wslPath}");
                Assert.True(exitCode <= 1, $"fsck.erofs (lzma big-pcluster) failed (exit={exitCode}):\n{output}");
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void Erofs_ExtractAndVerify()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("dump.erofs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(root, "b.txt"), "beta");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "c.txt"), "gamma");

            var builder = new ErofsBuilder();
            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-erofs-ext-{Path.GetRandomFileName()}.img");
            try
            {
                builder.Build(root, imagePath);
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                var (lsOutput, lsExit) = WslTestHelpers.RunWsl($"dump.erofs --ls --path=/ {wslPath}");
                Assert.True(lsExit == 0, $"dump.erofs --ls failed:\n{lsOutput}");
                Assert.Contains("a.txt", lsOutput);
                Assert.Contains("b.txt", lsOutput);
                Assert.Contains("nested", lsOutput);

                var (catA, catAExit) = WslTestHelpers.RunWsl($"dump.erofs --cat --path=/a.txt {wslPath}");
                Assert.True(catAExit == 0, $"dump.erofs --cat /a.txt failed:\n{catA}");
                Assert.Contains("alpha", catA);

                var (catB, catBExit) = WslTestHelpers.RunWsl($"dump.erofs --cat --path=/b.txt {wslPath}");
                Assert.True(catBExit == 0, $"dump.erofs --cat /b.txt failed:\n{catB}");
                Assert.Contains("beta", catB);

                var (catC, catCExit) = WslTestHelpers.RunWsl($"dump.erofs --cat --path=/nested/c.txt {wslPath}");
                Assert.True(catCExit == 0, $"dump.erofs --cat /nested/c.txt failed:\n{catC}");
                Assert.Contains("gamma", catC);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void F2fs_FsckF2fs_PassesOnBuiltImage()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("fsck.f2fs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello f2fs\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), [0x00, 0x01, 0x02, 0xFF]);
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "inner.txt"), "inner");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-f2fs-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath);
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                var (output, exitCode) = WslTestHelpers.RunWsl($"fsck.f2fs --dry-run {wslPath}");
                AssertF2fsClean(output, exitCode);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void F2fs_DumpF2fs_ShowsCorrectSuperblock()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("dump.f2fs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "test.txt"), "test");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-f2fs-dump-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath);
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                var (output, exitCode) = WslTestHelpers.RunWsl($"dump.f2fs {wslPath} 2>&1 | head -30");
                Assert.True(exitCode == 0, $"dump.f2fs failed:\n{output}");
                Assert.Contains("Info: superblock features", output);
                Assert.Contains("Info: total FS sectors", output);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void Ext4_WslRoundTrip_UnpackAndRepack()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("debugfs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "original.txt"), "round trip test");
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            File.WriteAllText(Path.Combine(root, "dir", "nested.txt"), "nested data");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-rt-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath);
                var wslPath = WslTestHelpers.ToWslPath(imagePath);

                var extractDir = Path.Combine(Path.GetTempPath(), $"wsl-ext4-extract-{Path.GetRandomFileName()}");
                Directory.CreateDirectory(extractDir);
                var wslExtractDir = WslTestHelpers.ToWslPath(extractDir);

                var (_, extractExit) = WslTestHelpers.RunWsl($"debugfs -R 'rdump / {wslExtractDir}' {wslPath}");
                Assert.True(extractExit == 0, "debugfs rdump failed");

                Assert.True(File.Exists(Path.Combine(extractDir, "original.txt")));
                Assert.Equal("round trip test", File.ReadAllText(Path.Combine(extractDir, "original.txt")));

                WslTestHelpers.DeleteRecursive(extractDir);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void Ext4_E2fsck_HtreeLargeDirectory_Passes()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("e2fsck")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            // 2000 项目录（触发 htree）+ 深层嵌套。
            // A 2000-entry directory (htree) plus deep nesting.
            var many = Path.Combine(root, "many");
            Directory.CreateDirectory(many);
            for (var i = 0; i < 2000; i++)
            {
                File.WriteAllText(Path.Combine(many, $"file_{i:D5}.dat"), $"content {i}\n");
            }

            var deep = Path.Combine(root, "deep");
            for (var i = 0; i < 25; i++)
            {
                deep = Path.Combine(deep, $"level{i:D2}");
            }

            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "bottom.txt"), "deep");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-htree-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath);
                var (output, exitCode) = WslTestHelpers.RunWsl($"e2fsck -fn {WslTestHelpers.ToWslPath(imagePath)}");
                Assert.True(exitCode <= 1, $"e2fsck failed (exit={exitCode}):\n{output}");
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void Ext4_E2fsck_MultiGroup_Passes()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("e2fsck")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            // 多块组（>128MB 数据 → ≥2 组），覆盖备份超级块/GDT 路径。
            // Multiple block groups (>128 MB data → ≥2 groups), covering backup superblock/GDT.
            for (var i = 0; i < 8; i++)
            {
                File.WriteAllBytes(Path.Combine(root, $"big{i:00}.bin"), new byte[24 * 1024 * 1024]);
            }

            File.WriteAllText(Path.Combine(root, "small.txt"), "multi group");
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            File.WriteAllText(Path.Combine(root, "dir", "n.txt"), "nested");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-multi-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath, new Ext4Options { EnableSparse = true });
                var (output, exitCode) = WslTestHelpers.RunWsl($"e2fsck -fn {WslTestHelpers.ToWslPath(imagePath)}");
                Assert.True(exitCode <= 1, $"e2fsck failed (exit={exitCode}):\n{output}");
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void F2fs_Fsck_Compression_Passes()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("fsck.f2fs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            // 可压缩 + 不可压缩混合（跨簇压缩与逐块回退路径）。
            // Mixed compressible / incompressible files (cluster compression and per-block fallback).
            File.WriteAllBytes(Path.Combine(root, "compressible.bin"), new byte[200000]);
            var random = new byte[200000];
            new Random(5).NextBytes(random);
            File.WriteAllBytes(Path.Combine(root, "incompressible.bin"), random);
            File.WriteAllText(Path.Combine(root, "tiny.txt"), "tiny");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-f2fs-comp-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath, new F2fsOptions { EnableCompression = true });
                var (output, exitCode) = WslTestHelpers.RunWsl($"fsck.f2fs --dry-run {WslTestHelpers.ToWslPath(imagePath)} 2>&1");
                AssertF2fsClean(output, exitCode);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void F2fs_Fsck_ManyInlineFiles_Passes()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("fsck.f2fs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            // 1000 个 inline 小文件 + 多块目录（多段主区与多块 NAT）。
            // 1000 inline files plus a multi-block directory (multi-segment main area, multi-block NAT).
            Directory.CreateDirectory(Path.Combine(root, "many"));
            for (var i = 0; i < 1000; i++)
            {
                File.WriteAllText(Path.Combine(root, "many", $"item_{i:D4}.txt"), $"payload {i}");
            }

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-f2fs-many-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath);
                var (output, exitCode) = WslTestHelpers.RunWsl($"fsck.f2fs --dry-run {WslTestHelpers.ToWslPath(imagePath)} 2>&1");
                AssertF2fsClean(output, exitCode);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    [Fact]
    public void F2fs_Fsck_LargeFileIndirect_Passes()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("fsck.f2fs")) { Assert.Skip("required tools missing"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            // 16MB 随机文件（跨越直接/间接寻址边界）+ 常规树。
            // A 16 MB random file (crossing the direct/indirect addressing boundary) plus a tree.
            var data = new byte[16 * 1024 * 1024];
            new Random(11).NextBytes(data);
            File.WriteAllBytes(Path.Combine(root, "large.bin"), data);
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello f2fs");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "inner.txt"), "inner");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-f2fs-large-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath);
                var (output, exitCode) = WslTestHelpers.RunWsl($"fsck.f2fs --dry-run {WslTestHelpers.ToWslPath(imagePath)} 2>&1");
                AssertF2fsClean(output, exitCode);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }
}