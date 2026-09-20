using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Boot-level proof for the managed EROFS multi-device writer. Device-table EROFS is an incompatible
/// superblock feature that the Linux kernel only learned to mount from 5.13 onward, so an era-5.10
/// guest cannot mount it by design; this test therefore drives the era-matched <em>device-table-capable</em>
/// QEMU guest shipped with the mount matrix and checks a genuine read-only mount plus whole-tree
/// SHA-256 read-back, which is what exercises the on-disk <c>erofs_device_info</c> layout the kernel
/// parses from the superblock. It is opt-in because it boots a kernel.
/// <para>托管 EROFS 多设备写入器的启动级证明。设备表 EROFS 是内核从 5.13 起才支持挂载的不兼容超级块
/// 特性，5.10 时代的客户机按设计无法挂载它；因此本测试使用挂载矩阵自带的、支持设备表的时代匹配 QEMU
/// 客户机，验证真实的只读挂载与整树 SHA-256 回读——即内核从超级块解析 <c>erofs_device_info</c> 磁盘布局
/// 的路径。它会启动内核，故为显式启用项。</para>
/// </summary>
public sealed class ErofsMultiDeviceMountTests : IDisposable
{
    private readonly string? _retain;
    private readonly string _root;
    private bool _failed;

    public ErofsMultiDeviceMountTests()
    {
        _retain = Environment.GetEnvironmentVariable("UOTAN_EROFS_MULTI_RETAIN");
        _root = string.IsNullOrWhiteSpace(_retain)
            ? Path.Combine(Path.GetTempPath(), "uotan-erofs-multi-mount-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(_retain!, "erofs-multi-mount");
    }

    [Fact]
    public void ManagedMultiDeviceImage_MountsAndReadsBackOnDeviceTableGuest()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UOTAN_FIRMWARE_QEMU_ENABLED")))
            Assert.Skip("Device-table mount verification is opt-in; set UOTAN_FIRMWARE_QEMU_ENABLED=1 to run it.");
        if (!WslTestHelpers.IsAlpineAvailable())
            Assert.Skip($"WSL distribution '{WslTestHelpers.DistroName}' is unavailable.");
        var script = ScriptPath("run-qemu.sh");
        var probe = RunScript(script, "--probe", 30000);
        if (probe.ExitCode == 77) Assert.Skip(probe.Output.Trim());
        Assert.True(probe.ExitCode == 0, $"QEMU probe failed.\n{probe.Output}");

        Directory.CreateDirectory(_root);
        FirmwareMountImages.Create(_root, ["erofs-flat", "erofs-multi-device", "erofs-legacy-flat"]);
        var result = RunScript(script, WslTestHelpers.ToWslPath(_root), 300000);
        var consolePath = Path.Combine(_root, "logs", "qemu-console.log");
        var console = File.Exists(consolePath) ? File.ReadAllText(consolePath) : string.Empty;
        try
        {
            Assert.True(result.ExitCode == 0,
                $"QEMU mount run failed (exit {result.ExitCode}). Artifacts: {_root}\n{result.Output}\n{console}");
            Assert.Contains("IMAGES_UNCHANGED", result.Output);
            // The device-table image must mount and read back whole-tree identical: this is the kernel
            // actually parsing the declared device table from the superblock.
            // 设备表镜像必须挂载并整树逐字节回读一致：这证明内核确实从超级块解析了声明的设备表。
            Assert.Contains("RESULT erofs-multi-device PASS", result.Output);
            Assert.Contains("RESULT erofs-flat PASS", result.Output);
            Assert.Contains("RESULT erofs-legacy-flat PASS", result.Output);
            Assert.DoesNotContain("MOUNT_SUMMARY", console.Split('\n')
                .Where(line => line.StartsWith("MOUNT_SUMMARY ", StringComparison.Ordinal)
                    && !line.EndsWith("failed=0", StringComparison.Ordinal)));
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private static (string Output, int ExitCode) RunScript(string script, string argument, int timeoutMs)
    {
        var path = WslTestHelpers.ToWslPath(script);
        var runtime = Environment.GetEnvironmentVariable("UOTAN_FIRMWARE_QEMU_ROOT");
        return WslTestHelpers.RunWslExecutable("env",
            [.. runtime is null ? Array.Empty<string>() : new[] { "UOTAN_FIRMWARE_QEMU_ROOT=" + runtime }, "sh", path, argument],
            timeoutMs, asRoot: true);
    }

    private static string ScriptPath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "FirmwareMountCheck", name);
        if (!File.Exists(path)) throw new FileNotFoundException("A bundled mount-test script is missing.", path);
        return path;
    }

    /// <summary>Removes the temporary run directory unless retention was requested or the check failed. <para>除非请求保留或检查失败，否则清理临时目录。</para></summary>
    public void Dispose()
    {
        if (!string.IsNullOrWhiteSpace(_retain)) return;
        if (!_failed && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}