using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies the managed filesystem builders against the official Google GKI 5.10
/// kernel in QEMU: read-write mounts for ext4 and f2fs, a read-only tree
/// verification plus fsck.erofs for EROFS, and a fsck pass for every filesystem.
/// The GKI kernel has no virtio block driver, so images ride inside the initramfs
/// and attach through loop devices.
/// <para>用谷歌官方 GKI 5.10 内核在 QEMU 中验证托管文件系统构建：ext4 与 f2fs 读写
/// 挂载，EROFS 只读目录树校验加 fsck.erofs，每种文件系统均运行 fsck。GKI 内核没有
/// virtio 块驱动，因此镜像嵌入 initramfs 并经 loop 设备接入。</para>
/// </summary>
public sealed class Gki510MountTests
{
    /// <summary>Runs the whole GKI matrix in one QEMU boot and asserts per-case results. <para>一次 QEMU 启动运行整个 GKI 矩阵并逐用例断言。</para></summary>
    [Fact]
    public void ManagedImages_MountAndFsck_UnderGki510Kernel()
    {
        var (kernel, reason) = LocateGkiKernel();
        if (reason is not null) Assert.Skip(reason);
        var root = Path.Combine(Path.GetTempPath(), "uotan-gki510-mount-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            // One representative case per filesystem: ext4 read-write, f2fs
            // read-write (its compression needs era-matched mkfs options), and
            // EROFS LZ4, which the 5.10 kernel supports built in.
            // 每种文件系统一个代表用例：ext4 读写、f2fs 读写（其压缩需要时代匹配的
            // mkfs 选项）、以及 5.10 内核内建支持的 EROFS LZ4。
            var cases = new (string Name, string Format, string Operation)[]
            {
                ("ext4-default", "ext4", "rw"),
                ("f2fs-flat", "f2fs", "rw"),
                ("erofs-lz4", "erofs", "ro"),
            };
            var source = Path.Combine(root, "source");
            FirmwareMountImages.CreateSource(root);
            var images = Path.Combine(root, "images");
            Directory.CreateDirectory(images);
            var lines = new List<string>();
            foreach (var (name, format, _) in cases)
            {
                var path = Path.Combine(images, name + ".img");
                BuildImage(name, source, path);
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                lines.Add($"{hash}  images/{name}.img");
            }
            File.WriteAllText(Path.Combine(root, "images.sha256"), string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "gki-cases.tsv"),
                string.Join('\n', cases.Select(item => $"{item.Name}\t{item.Format}\t{item.Operation}")) + "\n",
                new UTF8Encoding(false));
            var result = RunScript(root, kernel);
            var consolePath = Path.Combine(root, "logs", "qemu-console.log");
            var console = File.Exists(consolePath) ? File.ReadAllText(consolePath) : string.Empty;
            Assert.True(result.ExitCode == 0,
                $"GKI 5.10 mount verification failed (exit {result.ExitCode}). Artifacts: {root}\n{result.Output}\n{console}");
            Assert.Contains("GKI_RW_PASS", result.Output);
            foreach (var (name, _, _) in cases)
                Assert.Contains($"RESULT {name} PASS", console);
            Assert.Equal(cases.Length, console.Split('\n').Count(line => line.StartsWith("WRITEBACK_VERIFIED ", StringComparison.Ordinal)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup; failed runs keep artifacts for diagnosis. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void BuildImage(string name, string source, string path)
    {
        switch (name)
        {
            case "ext4-default":
                Ext4Builder.Build(source, path);
                break;
            case "erofs-lz4":
                new ErofsBuilder().Build(source, path, new ErofsOptions { Algorithm = ErofsCompressionAlgorithm.Lz4 });
                break;
            case "f2fs-flat":
                F2fsBuilder.Build(source, path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown GKI case.");
        }
    }

    /// <summary>Locates the official GKI 5.10 kernel; explains the skip when absent. <para>定位官方 GKI 5.10 内核；缺失时给出跳过原因。</para></summary>
    private static (string Kernel, string? SkipReason) LocateGkiKernel()
    {
        var kernel = Environment.GetEnvironmentVariable("UOTAN_FIRMWARE_GKI_KERNEL");
        if (string.IsNullOrWhiteSpace(kernel))
            kernel = "/tmp/uotan-gki510/bzImage";
        var check = WslTestHelpers.RunWsl($"[ -r '{WslTestHelpers.Quote(kernel)}' ] && echo present || echo absent", 15000);
        if (check.Output.Trim() != "present")
            return (string.Empty,
                "The official android13-5.10 GKI kernel (5.10.257-android13, CI build 15876359) is missing in WSL; "
                + $"expected at {kernel} or via UOTAN_FIRMWARE_GKI_KERNEL.");
        return (kernel, null);
    }

    private static (string Output, int ExitCode) RunScript(string runDirectory, string kernel)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "FirmwareMountCheck", "run-gki-rw.sh");
        if (!File.Exists(script)) throw new FileNotFoundException("The bundled GKI mount script is missing.", script);
        return WslTestHelpers.RunWslExecutable("env",
            ["UOTAN_FIRMWARE_GKI_KERNEL=" + kernel, "sh", WslTestHelpers.ToWslPath(script), WslTestHelpers.ToWslPath(runDirectory)],
            480000, asRoot: true);
    }
}
