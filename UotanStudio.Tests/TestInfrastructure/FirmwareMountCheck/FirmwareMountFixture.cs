using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Shares one isolated QEMU boot across the filesystem matrix. <para>文件系统矩阵共用一次隔离 QEMU 启动。</para></summary>
public sealed class FirmwareMountFixture : IDisposable
{
    internal static readonly string[] CaseNames =
    [
        "ext4-default", "ext4-no-journal", "erofs-flat", "erofs-lz4", "erofs-lzma",
        "erofs-deflate", "erofs-zstd", "erofs-lz4-full-index", "erofs-fragments-dedup",
        "erofs-metabox", "f2fs-flat", "f2fs-lz4", "f2fs-zstd", "f2fs-lzo"
    ];

    private readonly Lazy<(string Output, int ExitCode)> _qemuProbe;
    private readonly Lazy<(string Output, int ExitCode)> _nativeProbe;
    private readonly Lazy<string> _qemuResult;
    private readonly Lazy<string> _nativeResult;
    private readonly Lazy<string> _root;
    private readonly bool _retain;
    private bool _failed;

    /// <summary>Defers image generation until dependencies are available. <para>依赖检查通过后才生成镜像。</para></summary>
    public FirmwareMountFixture()
    {
        var artifacts = Environment.GetEnvironmentVariable("UOTAN_FIRMWARE_TEST_ARTIFACTS");
        _retain = !string.IsNullOrWhiteSpace(artifacts);
        _root = new Lazy<string>(() =>
        {
            var root = Path.Combine(_retain ? Path.GetFullPath(artifacts!) : Path.GetTempPath(), "firmware-mount-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        });
        _qemuProbe = new(() => Probe("run-qemu.sh"));
        _nativeProbe = new(() => Probe("run-native-deflate.sh"));
        _qemuResult = new(RunQemu);
        _nativeResult = new(RunNative);
    }

    /// <summary>
    /// The heavy QEMU mount matrix is opt-in: it boots up to 14 kernel guests and must not
    /// stall the default test flow. Set <c>UOTAN_FIRMWARE_QEMU_ENABLED</c> to run it.
    /// <para>重型 QEMU 挂载矩阵为显式启用项：它最多启动 14 个内核客户机，不应阻塞默认测试流程。
    /// 设置 <c>UOTAN_FIRMWARE_QEMU_ENABLED</c> 才运行。</para>
    /// </summary>
    private static bool QemuEnabled()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UOTAN_FIRMWARE_QEMU_ENABLED"));

    internal string VerifyQemu(string name)
    {
        Assert.Contains(name, CaseNames);
        if (!QemuEnabled())
            Assert.Skip("QEMU mount verification is opt-in; set UOTAN_FIRMWARE_QEMU_ENABLED=1 to run the mount matrix.");
        var unavailable = QemuUnavailableReason(name);
        if (unavailable is not null) Assert.Skip(unavailable);
        try
        {
            var result = _qemuResult.Value;
            Assert.True(result.Split('\n').Any(line => line.Trim() == $"RESULT {name} PASS"),
                $"QEMU case {name} failed.\n{result}");
            return string.Join('\n', result.Split('\n').Where(line => line.StartsWith("Artifacts:", StringComparison.Ordinal)
                || line.StartsWith("RESULT ", StringComparison.Ordinal) || line.StartsWith("MOUNT_SUMMARY ", StringComparison.Ordinal)
                || line.Trim() == "IMAGES_UNCHANGED").Distinct());
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    internal string VerifyNativeDeflate()
    {
        if (!QemuEnabled())
            Assert.Skip("Native DEFLATE verification is opt-in; set UOTAN_FIRMWARE_QEMU_ENABLED=1 to run it.");
        var probe = _nativeProbe.Value;
        if (probe.ExitCode == 77) Assert.Skip(probe.Output.Trim());
        Assert.True(probe.ExitCode == 0, probe.Output);
        try { return _nativeResult.Value; }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private string? QemuUnavailableReason(string name)
    {
        var probe = _qemuProbe.Value;
        if (probe.ExitCode == 77) return probe.Output.Trim();
        Assert.True(probe.ExitCode == 0, probe.Output);
        var lines = probe.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var (config, module) in new[]
        {
            ("CONFIG_VIRTIO_PCI", "virtio_pci"), ("CONFIG_VIRTIO_BLK", "virtio_blk"),
            (name.StartsWith("ext4", StringComparison.Ordinal) ? "CONFIG_EXT4_FS" :
                name.StartsWith("erofs", StringComparison.Ordinal) ? "CONFIG_EROFS_FS" : "CONFIG_F2FS_FS", name.Split('-')[0])
        })
        {
            if (!lines.Contains(config + "=y") && !(lines.Contains(config + "=m") && lines.Contains("MODULE " + module)))
                return $"QEMU guest capability {config} or its module is unavailable.";
        }
        var required = new List<string> { "CONFIG_BLK_DEV_INITRD", "CONFIG_RD_GZIP", "CONFIG_DEVTMPFS" };
        if (name.StartsWith("erofs-", StringComparison.Ordinal) && name != "erofs-flat")
            required.Add("CONFIG_EROFS_FS_ZIP");
        if (name is "erofs-lzma" or "erofs-deflate" or "erofs-zstd")
            required.Add("CONFIG_EROFS_FS_ZIP_" + name[6..].ToUpperInvariant());
        if (name.StartsWith("f2fs-", StringComparison.Ordinal) && name != "f2fs-flat")
        {
            required.Add("CONFIG_F2FS_FS_COMPRESSION");
            required.Add("CONFIG_F2FS_FS_" + name[5..].ToUpperInvariant());
        }
        foreach (var config in required)
            if (!lines.Contains(config + "=y")) return $"QEMU guest capability {config} is unavailable.";
        // Metabox lacks a Kconfig switch; use the oldest kernel validated by this matrix.
        if (name == "erofs-metabox")
        {
            var versionLine = lines.Single(line => line.StartsWith("KERNEL_VERSION ", StringComparison.Ordinal));
            var version = Version.Parse(versionLine["KERNEL_VERSION ".Length..].Split('-')[0]);
            if (version < new Version(6, 18)) return "The metabox mount check requires a Linux 6.18 or newer guest.";
        }
        return null;
    }

    private static (string Output, int ExitCode) Probe(string script)
    {
        _ = ScriptPath(script);
        _ = ScriptPath("guest-init.sh");
        if (!WslTestHelpers.IsAlpineAvailable())
            return ($"WSL distribution '{WslTestHelpers.DistroName}' is unavailable.", 77);
        var user = WslTestHelpers.RunWslExecutable("id", ["-u"], asRoot: script == "run-qemu.sh");
        if (user.ExitCode != 0)
            return ($"The required WSL user cannot run the probe: {user.Output}", 77);
        return RunScript(script, "--probe", 30000);
    }

    private string RunQemu()
    {
        var root = Path.Combine(_root.Value, "qemu");
        var cases = CaseNames.Where(name => QemuUnavailableReason(name) is null).ToArray();
        FirmwareMountImages.Create(root, cases);
        var result = RunScript("run-qemu.sh", WslTestHelpers.ToWslPath(root), 300000);
        var consolePath = Path.Combine(root, "logs", "qemu-console.log");
        var console = File.Exists(consolePath) ? File.ReadAllText(consolePath) : string.Empty;
        Assert.True(result.ExitCode is 0 or 1, $"QEMU verification failed (exit {result.ExitCode}). Artifacts: {root}\n{result.Output}\n{console}");
        Assert.Contains("IMAGES_UNCHANGED", result.Output);
        Assert.Single(console.Split('\n'), line => line.StartsWith("MOUNT_SUMMARY ", StringComparison.Ordinal));
        Assert.Equal(cases.Length, console.Split('\n').Count(line => line.StartsWith("RESULT ", StringComparison.Ordinal)));
        foreach (var name in cases)
            Assert.Single(console.Split('\n'), line => line.StartsWith($"RESULT {name} ", StringComparison.Ordinal));
        return $"Artifacts: {root}\n{result.Output}\n{console}";
    }

    private string RunNative()
    {
        var root = Path.Combine(_root.Value, "native");
        FirmwareMountImages.Create(root, ["erofs-deflate"]);
        var result = RunScript("run-native-deflate.sh", WslTestHelpers.ToWslPath(root), 60000);
        var logPath = Path.Combine(root, "logs", "native-erofs-deflate.log");
        var log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        Assert.True(result.ExitCode == 0, $"Native DEFLATE verification failed (exit {result.ExitCode}). Artifacts: {root}\n{result.Output}\n{log}");
        Assert.Contains("NATIVE_DEFLATE_VERIFY_PASS", log);
        return $"Artifacts: {root}\nNATIVE_DEFLATE_VERIFY_PASS";
    }

    private static (string Output, int ExitCode) RunScript(string script, string argument, int timeoutMs)
    {
        var path = WslTestHelpers.ToWslPath(ScriptPath(script));
        var runtime = Environment.GetEnvironmentVariable("UOTAN_FIRMWARE_QEMU_ROOT");
        return WslTestHelpers.RunWslExecutable("env",
            [.. runtime is null ? Array.Empty<string>() : new[] { "UOTAN_FIRMWARE_QEMU_ROOT=" + runtime }, "sh", path, argument],
            timeoutMs, asRoot: script == "run-qemu.sh");
    }

    private static string ScriptPath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "FirmwareMountCheck", name);
        if (!File.Exists(path)) throw new FileNotFoundException("A bundled mount-test script is missing.", path);
        return path;
    }

    /// <summary>Removes successful temporary runs unless retention was requested. <para>未指定保留目录时清理成功运行的临时产物。</para></summary>
    public void Dispose()
    {
        if (_root.IsValueCreated && !_retain && !_failed) Directory.Delete(_root.Value, recursive: true);
    }
}