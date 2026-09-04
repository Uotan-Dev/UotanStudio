using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// WSL 挂载级交叉验证：构建 EXT4/EROFS/F2FS 镜像后在 WSL 中以 loop 设备挂载，验证内核可识别
/// 镜像布局并正确读回文件内容。与工具级验证（e2fsck/fsck.*）互补，覆盖真实内核挂载路径。
/// <para>WSL mount-level cross-validation: builds EXT4/EROFS/F2FS images, mounts them via a loop
/// device inside WSL, and verifies the kernel recognizes the layout and reads file contents back
/// correctly. Complements tool-level validation (e2fsck/fsck.*) by exercising the real kernel
/// mount path.</para>
/// </summary>
/// <remarks>
/// 环境限制：当前 WSL（Debian trixie）为 WSL1 内核——免密 sudo 可用但 loop 设备 setup 失败、
/// f2fs/erofs 内核模块缺失。因此这些用例在能力探测不满足时以 <see cref="Assert.Skip"/>
/// 跳过；在支持 loop mount 的 WSL2/原生 Linux 环境下会真实执行。
/// <para>Environment limits: the current WSL (Debian trixie) runs a WSL1 kernel — passwordless sudo
/// works but loop-device setup fails and the f2fs/erofs kernel modules are absent. These cases
/// therefore skip via <see cref="Assert.Skip"/> when the capability probe fails; on WSL2 / native
/// Linux with loop-mount support they execute for real.</para>
/// </remarks>
public class WslMountTests
{
    /// <summary>
    /// WSL 可用时返回 true。
    /// <para>Returns true when WSL is available.</para>
    /// </summary>
    private static bool IsWslAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl", "--status")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在 WSL 中执行命令，返回 stdout+stderr 与退出码。
    /// <para>Runs a command inside WSL and returns stdout+stderr with the exit code.</para>
    /// </summary>
    private static (string Output, int ExitCode) RunWsl(string command, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo("wsl", $"-- bash -c \"{command.Replace("\"", "\\\"")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        return (stdout + stderr, proc.ExitCode);
    }

    /// <summary>
    /// 将 Windows 路径转换为 WSL 路径（D:\a\b → /mnt/d/a/b）。
    /// <para>Converts a Windows path to a WSL path (D:\a\b → /mnt/d/a/b).</para>
    /// </summary>
    private static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        if (full.Length >= 2 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }

        return full.Replace('\\', '/');
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-wsl-mount-" + Path.GetRandomFileName());
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

    private static bool? _loopMountProbe;

    /// <summary>
    /// 探测 WSL 是否支持 loop 挂载：真实创建一个小 ext4 镜像并尝试 loop 挂载（结果缓存）。
    /// 仅凭 <c>losetup -f</c> 能返回设备不够——WSL1 下 loop 设备 setup 实际失败。
    /// <para>Probes whether WSL supports loop mounts by actually creating and mounting a small
    /// ext4 image (cached). <c>losetup -f</c> alone is insufficient — WSL1 fails to set up the
    /// loop device in practice.</para>
    /// </summary>
    private static bool CanLoopMount()
    {
        if (_loopMountProbe.HasValue)
        {
            return _loopMountProbe.Value;
        }

        var script =
            "td=$(mktemp -d) && " +
            "truncate -s 8M \"$td/t.img\" && " +
            "mke2fs -q -t ext4 \"$td/t.img\" >/dev/null 2>&1 && " +
            "mkdir -p \"$td/mnt\" && " +
            "if sudo -n mount -o loop \"$td/t.img\" \"$td/mnt\" >/dev/null 2>&1; then " +
            "  sudo -n umount \"$td/mnt\" >/dev/null 2>&1; rm -rf \"$td\"; echo LOOP_OK; " +
            "else rm -rf \"$td\"; echo LOOP_FAIL; fi";
        var (output, exitCode) = RunWsl(script, 30000);
        _loopMountProbe = exitCode == 0 && output.Contains("LOOP_OK", StringComparison.Ordinal);
        return _loopMountProbe.Value;
    }

    /// <summary>
    /// 探测内核是否支持指定文件系统（/proc/filesystems 含目标类型）。
    /// <para>Probes whether the kernel supports the given filesystem (present in /proc/filesystems).</para>
    /// </summary>
    private static bool KernelSupports(string fsType)
    {
        var (output, _) = RunWsl($"grep -w {fsType} /proc/filesystems 2>&1", 15000);
        return output.Contains(fsType, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在 WSL 内挂载镜像并验证文件读回，随后卸载。返回 (成功?, 输出)。
    /// <para>Mounts the image inside WSL, verifies file read-back, then unmounts. Returns (ok?, output).</para>
    /// </summary>
    private static (bool Ok, string Output) MountVerifyAndUnmount(string imageWslPath, string fsType, string[] expectedFiles)
    {
        var mountPoint = "/tmp/uotan-mount-check";
        var script = new StringBuilder();
        script.Append($"rm -rf {mountPoint} && mkdir -p {mountPoint} && ");
        script.Append($"sudo -n mount -t {fsType} -o loop,ro {imageWslPath} {mountPoint} 2>&1 && ");
        foreach (var file in expectedFiles)
        {
            script.Append($"cat {mountPoint}/{file} 2>&1 && ");
        }

        script.Append($"sudo -n umount {mountPoint} 2>&1 && rm -rf {mountPoint}");
        var (output, exitCode) = RunWsl(script.ToString(), 60000);
        return (exitCode == 0, output);
    }

    [Fact]
    public void Ext4_MountLoop_ReadBackFiles()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!CanLoopMount()) { Assert.Skip("loop mount unavailable (needs passwordless sudo + loop device)"); }

        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "mount test\n");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "inner.txt"), "inner data");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-mount-ext4-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath);
                var (ok, output) = MountVerifyAndUnmount(
                    ToWslPath(imagePath),
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
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Erofs_MountLoop_ReadBackFiles()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!CanLoopMount()) { Assert.Skip("loop mount unavailable (needs passwordless sudo + loop device)"); }
        if (!KernelSupports("erofs")) { Assert.Skip("erofs kernel module not present"); }

        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha erofs\n");
            File.WriteAllText(Path.Combine(root, "b.txt"), "beta erofs");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-mount-erofs-{Path.GetRandomFileName()}.img");
            try
            {
                var builder = new ErofsBuilder();
                builder.Build(root, imagePath, new ErofsOptions { Compress = false });
                var (ok, output) = MountVerifyAndUnmount(ToWslPath(imagePath), "erofs", ["a.txt", "b.txt"]);
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
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void F2fs_MountLoop_ReadBackFiles()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!CanLoopMount()) { Assert.Skip("loop mount unavailable (needs passwordless sudo + loop device)"); }
        if (!KernelSupports("f2fs")) { Assert.Skip("f2fs kernel module not present"); }

        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "f2fs mount\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), "binary\0data"u8.ToArray());

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-mount-f2fs-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath);
                var (ok, output) = MountVerifyAndUnmount(ToWslPath(imagePath), "f2fs", ["hello.txt", "blob.bin"]);
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
            DeleteRecursive(root);
        }
    }
}