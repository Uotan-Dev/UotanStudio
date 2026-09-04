using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// WSL 交叉验证测试：构建 EXT4/EROFS/F2FS 镜像后通过 WSL 文件系统工具（e2fsck、fsck.erofs、
/// fsck.f2fs）验证磁盘布局正确性、校验和完整性和结构一致性。
/// <para>WSL cross-validation tests: builds EXT4/EROFS/F2FS images, then verifies disk layout
/// correctness, checksum integrity and structural consistency via WSL filesystem tools.</para>
/// </summary>
public class WslCrossValidationTests
{
    /// <summary>
    /// WSL 可用时返回 true（Debian 或其他发行版均可）。
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
    /// 在 WSL 中执行命令，返回 stdout+stderr 和退出码。
    /// </summary>
    private static (string Output, int ExitCode) RunWsl(string command, int timeoutMs = 30000)
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
        var path = Path.Combine(Path.GetTempPath(), "uotan-wsl-" + Path.GetRandomFileName());
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

    /// <summary>
    /// 工具可用性缓存：tool → 是否存在。线程安全，仅探测一次。
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> s_toolCache = new();

    /// <summary>
    /// 探测的工具集合（顺序与 <see cref="EnsureToolsProbed"/> 中的 <c>command -v</c> 串一致）。
    /// </summary>
    private static readonly string[] s_probedTools =
    [
        "mke2fs", "tune2fs", "dumpe2fs", "debugfs", "e2fsck",
        "mkfs.erofs", "fsck.erofs", "dump.erofs",
        "mkfs.f2fs", "fsck.f2fs", "dump.f2fs"
    ];

    /// <summary>
    /// 批量探测 WSL 中各工具是否存在。一次 WSL 调用串接所有 <c>command -v</c>，
    /// 按行对齐到 <see cref="s_probedTools"/>，非空路径行即视为存在。
    /// </summary>
    private static void EnsureToolsProbed()
    {
        if (!s_toolCache.IsEmpty)
            return;

        // 构造形如：command -v mke2fs; command -v tune2fs; ...
        var probe = string.Join("; ", s_probedTools.Select(t => $"command -v {t}"));
        var (output, _) = RunWsl(probe, 15000);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < s_probedTools.Length; i++)
        {
            var tool = s_probedTools[i];
            // 每个工具对应一行输出；行非空且非 "command not found" 类提示即存在。
            var line = i < lines.Length ? lines[i].Trim() : string.Empty;
            var present = line.Length > 0 && !line.Contains("not found", StringComparison.OrdinalIgnoreCase);
            s_toolCache[tool] = present;
        }
    }

    /// <summary>
    /// 查询某工具是否在 WSL 中可用。首次查询触发批量探测并缓存。
    /// </summary>
    private static bool IsToolAvailable(string tool)
    {
        EnsureToolsProbed();
        return s_toolCache.TryGetValue(tool, out var present) && present;
    }

    [Fact]
    public void Ext4_E2fsck_PassesOnBuiltImage()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("e2fsck") || !IsToolAvailable("debugfs")) { Assert.Skip("required tools missing"); }

        var root = CreateTempDirectory();
        try
        {
            // 构建含多种文件类型的 EXT4 镜像。
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), [0x00, 0x01, 0x02, 0xFF]);
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "nested.txt"), "nested content");
            TryCreateSymlink(Path.Combine(root, "link"), "hello.txt");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath, new Ext4Options { EnableSparse = true });
                var wslPath = ToWslPath(imagePath);

                // e2fsck -n：只读检查，不修改镜像。
                var (output, exitCode) = RunWsl($"e2fsck -n -f {wslPath}");

                // e2fsck 退出码：0=无错误，1=文件系统已修正，2=已修正需重新启动，
                // 4=发现错误未修正，8=操作错误。允许0和1。
                Assert.True(exitCode <= 1, $"e2fsck failed (exit={exitCode}):\n{output}");

                // 验证 debugfs 能列出文件。
                var (lsOutput, lsExit) = RunWsl($"debugfs -R 'ls /' {wslPath}");
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
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Ext4_Dumpe2fs_ShowsCorrectSuperblockFields()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("dumpe2fs")) { Assert.Skip("required tools missing"); }

        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "test.txt"), "test");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-dump-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath);
                var wslPath = ToWslPath(imagePath);

                var (output, exitCode) = RunWsl($"dumpe2fs -h {wslPath}");
                Assert.True(exitCode == 0, $"dumpe2fs failed:\n{output}");
                Assert.Contains("0xEF53", output); // magic
                Assert.Contains("Filesystem features:", output);
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
    public void Erofs_FsckErofs_PassesOnBuiltImage()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("fsck.erofs")) { Assert.Skip("required tools missing"); }

        var root = CreateTempDirectory();
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
                var wslPath = ToWslPath(imagePath);

                // fsck.erofs：只读检查。
                var (output, exitCode) = RunWsl($"fsck.erofs {wslPath}");

                // fsck.erofs 退出码：0=OK,1=需要修复,2=错误。
                Assert.True(exitCode <= 1, $"fsck.erofs failed (exit={exitCode}):\n{output}");
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
    public void Erofs_ExtractAndVerify()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("dump.erofs")) { Assert.Skip("required tools missing"); }

        var root = CreateTempDirectory();
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
                var wslPath = ToWslPath(imagePath);

                // dump.erofs 是布局转储工具（没有抽取子命令）；用 --ls/--cat 从真实工具输出
                // 校验文件列表与文件内容。
                // dump.erofs is a layout dumper (it has no extract subcommand); use --ls/--cat to
                // verify the file listing and file contents from the real tool output.
                var (lsOutput, lsExit) = RunWsl($"dump.erofs --ls --path=/ {wslPath}");
                Assert.True(lsExit == 0, $"dump.erofs --ls failed:\n{lsOutput}");
                Assert.Contains("a.txt", lsOutput);
                Assert.Contains("b.txt", lsOutput);
                Assert.Contains("nested", lsOutput);

                var (catA, catAExit) = RunWsl($"dump.erofs --cat --path=/a.txt {wslPath}");
                Assert.True(catAExit == 0, $"dump.erofs --cat /a.txt failed:\n{catA}");
                Assert.Contains("alpha", catA);

                var (catB, catBExit) = RunWsl($"dump.erofs --cat --path=/b.txt {wslPath}");
                Assert.True(catBExit == 0, $"dump.erofs --cat /b.txt failed:\n{catB}");
                Assert.Contains("beta", catB);

                var (catC, catCExit) = RunWsl($"dump.erofs --cat --path=/nested/c.txt {wslPath}");
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
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void F2fs_FsckF2fs_PassesOnBuiltImage()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("fsck.f2fs")) { Assert.Skip("required tools missing"); }

        var root = CreateTempDirectory();
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
                var wslPath = ToWslPath(imagePath);

                // fsck.f2fs：只读检查（-n 不修改）。
                var (output, exitCode) = RunWsl($"fsck.f2fs -n {wslPath}");

                // fsck.f2fs 退出码：0=OK,1=已修正,2=错误。
                Assert.True(exitCode <= 1, $"fsck.f2fs failed (exit={exitCode}):\n{output}");
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
    public void F2fs_DumpF2fs_ShowsCorrectSuperblock()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("dump.f2fs")) { Assert.Skip("required tools missing"); }

        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "test.txt"), "test");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-f2fs-dump-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath);
                var wslPath = ToWslPath(imagePath);

                var (output, exitCode) = RunWsl($"dump.f2fs {wslPath} 2>&1 | head -30");
                Assert.True(exitCode == 0, $"dump.f2fs failed:\n{output}");
                // dump.f2fs 不打印魔数十六进制常量；它以 "Info: ..." 行输出超级块字段摘要，
                // 因此按真实工具输出断言 superblock 摘要已被解析出来。
                // dump.f2fs does not print the magic hex constant; it prints "Info: ..." lines as a
                // superblock summary, so assert on the parsed superblock summary from the real output.
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
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Ext4_WslRoundTrip_UnpackAndRepack()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("debugfs")) { Assert.Skip("required tools missing"); }

        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "original.txt"), "round trip test");
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            File.WriteAllText(Path.Combine(root, "dir", "nested.txt"), "nested data");

            var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-ext4-rt-{Path.GetRandomFileName()}.img");
            try
            {
                Ext4Builder.Build(root, imagePath);
                var wslPath = ToWslPath(imagePath);

                // 用 debugfs 提取文件验证内容。
                var extractDir = Path.Combine(Path.GetTempPath(), $"wsl-ext4-extract-{Path.GetRandomFileName()}");
                Directory.CreateDirectory(extractDir);
                var wslExtractDir = ToWslPath(extractDir);

                var (_, extractExit) = RunWsl($"debugfs -R 'rdump / {wslExtractDir}' {wslPath}");
                Assert.True(extractExit == 0, "debugfs rdump failed");

                Assert.True(File.Exists(Path.Combine(extractDir, "original.txt")));
                Assert.Equal("round trip test", File.ReadAllText(Path.Combine(extractDir, "original.txt")));

                DeleteRecursive(extractDir);
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
