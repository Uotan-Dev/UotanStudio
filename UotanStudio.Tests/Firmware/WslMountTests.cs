using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// WSL2 Alpine 挂载级交叉验证：构建 EXT4/EROFS/F2FS 镜像后在 Alpine WSL2 中以 loop 设备
/// 挂载，验证内核可识别镜像布局并正确读回文件内容。与工具级验证（e2fsck/fsck.*）互补，
/// 覆盖真实内核挂载路径。
/// <para>WSL2 Alpine mount-level cross-validation: builds EXT4/EROFS/F2FS images, mounts them
/// via a loop device inside Alpine WSL2, and verifies the kernel recognizes the layout and reads
/// file contents back correctly. Complements tool-level validation (e2fsck/fsck.*) by exercising
/// the real kernel mount path.</para>
/// </summary>
/// <remarks>
/// 使用 Alpine WSL2 发行版（已自动配置 edge 仓库 + e2fsprogs-extra/erofs-utils/f2fs-tools）。
/// erofs/f2fs 内核模块通过 insmod 加载。能力探测不满足时以 <see cref="Assert.Skip"/> 跳过。
/// <para>Uses the Alpine WSL2 distribution (auto-configured with edge repos + e2fsprogs-extra/
/// erofs-utils/f2fs-tools). erofs/f2fs kernel modules are loaded via insmod. Cases skip
/// via <see cref="Assert.Skip"/> when capability probes fail.</para>
/// </remarks>
public class WslMountTests
{
    [Fact]
    public void Ext4_MountLoop_ReadBackFiles()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        WslTestHelpers.EnsureAlpineConfigured();
        if (!WslTestHelpers.CanLoopMount()) { Assert.Skip("loop mount unavailable"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "mount test\n");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "inner.txt"), "inner data");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-mount-ext4-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath);
                var (ok, _, output) = WslTestHelpers.MountVerifyAndUnmount(
                    WslTestHelpers.ToWslPath(imagePath),
                    "ext4",
                    ["hello.txt", "sub/inner.txt"]);
                Assert.True(ok, $"ext4 mount read-back failed:\n{output}");
                Assert.Contains("mount test", output);
                Assert.Contains("inner data", output);
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
    public void Erofs_MountLoop_ReadBackFiles()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        WslTestHelpers.EnsureAlpineConfigured();
        if (!WslTestHelpers.EnsureKernelModules()) { Assert.Skip("erofs kernel module could not be loaded"); }
        if (!WslTestHelpers.CanLoopMount()) { Assert.Skip("loop mount unavailable"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha erofs\n");
            File.WriteAllText(Path.Combine(root, "b.txt"), "beta erofs");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-mount-erofs-{Path.GetRandomFileName()}.img");
            try
            {
                var builder = new ErofsBuilder();
                builder.Build(root, imagePath, new ErofsOptions { Compress = false });
                var (ok, kind, output) = WslTestHelpers.MountVerifyAndUnmount(
                    WslTestHelpers.ToWslPath(imagePath),
                    "erofs",
                    ["a.txt", "b.txt"]);
                if (kind == WslTestHelpers.MountFailureKind.VersionMismatch)
                {
                    Assert.Skip($"erofs kernel module version mismatch with erofs-utils (kernel 6.18 vs erofs-utils 1.9.4): {output.Trim().Split('\n').LastOrDefault()}");
                }

                Assert.True(ok, $"erofs mount read-back failed:\n{output}");
                Assert.Contains("alpha erofs", output);
                Assert.Contains("beta erofs", output);
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
    public void F2fs_MountLoop_ReadBackFiles()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        WslTestHelpers.EnsureAlpineConfigured();
        if (!WslTestHelpers.EnsureKernelModules()) { Assert.Skip("f2fs kernel module could not be loaded"); }
        if (!WslTestHelpers.CanLoopMount()) { Assert.Skip("loop mount unavailable"); }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "f2fs mount\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), "binary\0data"u8.ToArray());

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-mount-f2fs-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath);
                var (ok, kind, output) = WslTestHelpers.MountVerifyAndUnmount(
                    WslTestHelpers.ToWslPath(imagePath),
                    "f2fs",
                    ["hello.txt", "blob.bin"]);
                if (kind == WslTestHelpers.MountFailureKind.VersionMismatch)
                {
                    Assert.Skip($"f2fs kernel module version mismatch with f2fs-tools: {output.Trim().Split('\n').LastOrDefault()}");
                }

                Assert.True(ok, $"f2fs mount read-back failed:\n{output}");
                Assert.Contains("f2fs mount", output);
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