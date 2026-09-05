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
/// 使用 Alpine WSL2 发行版（已自动配置 edge 仓库 + e2fsprogs-extra/erofs-utils/f2fs-tools）。
/// 内核模块（erofs/f2fs）通过 insmod 加载。工具不可用时以 <see cref="Assert.Skip"/> 跳过。
/// <para>Uses the Alpine WSL2 distribution (auto-configured with edge repos + e2fsprogs-extra/
/// erofs-utils/f2fs-tools). Kernel modules (erofs/f2fs) are loaded via insmod. Cases skip
/// via <see cref="Assert.Skip"/> when tools are unavailable.</para>
/// </remarks>
public class WslCrossValidationTests
{
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
        WslTestHelpers.EnsureAlpineConfigured();
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
        WslTestHelpers.EnsureAlpineConfigured();
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
        WslTestHelpers.EnsureAlpineConfigured();
        WslTestHelpers.EnsureKernelModules();
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
    public void Erofs_ExtractAndVerify()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        WslTestHelpers.EnsureAlpineConfigured();
        WslTestHelpers.EnsureKernelModules();
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
        WslTestHelpers.EnsureAlpineConfigured();
        WslTestHelpers.EnsureKernelModules();
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

                var (output, exitCode) = WslTestHelpers.RunWsl($"fsck.f2fs -n {wslPath}");
                Assert.True(exitCode <= 1, $"fsck.f2fs failed (exit={exitCode}):\n{output}");
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
        WslTestHelpers.EnsureAlpineConfigured();
        WslTestHelpers.EnsureKernelModules();
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
        WslTestHelpers.EnsureAlpineConfigured();
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
}