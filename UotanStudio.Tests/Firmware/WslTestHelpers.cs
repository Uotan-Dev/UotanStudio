using System.Collections.Concurrent;
using System.Diagnostics;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// WSL 测试共享基础设施：Alpine WSL2 发行版管理、命令执行、路径转换、工具探测与内核模块补全。
/// <para>Shared WSL test infrastructure: Alpine WSL2 distribution management, command execution,
/// path conversion, tool probing, and kernel module supplementation.</para>
/// </summary>
internal static class WslTestHelpers
{
    /// <summary>
    /// Alpine WSL2 发行版名称。
    /// <para>The Alpine WSL2 distribution name.</para>
    /// </summary>
    public const string DistroName = "Alpine";

    private static bool _alpineConfigured;
    private static bool _kernelModulesLoaded;
    private static readonly Lock ConfigLock = new();

    /// <summary>
    /// WSL 命令是否可用。
    /// <para>Whether the wsl command is available.</para>
    /// </summary>
    public static bool IsWslAvailable()
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
    /// Alpine 发行版是否已注册。
    /// <para>Whether the Alpine distribution is registered.</para>
    /// </summary>
    public static bool IsAlpineAvailable()
    {
        var (_, exitCode) = RunWsl("echo OK", 10000);
        return exitCode == 0;
    }

    /// <summary>
    /// 当前 WSL 是否为 WSL2 内核（含 "microsoft" 标记）。
    /// <para>Whether the current WSL runs a WSL2 kernel (contains "microsoft" tag).</para>
    /// </summary>
    public static bool IsWsl2()
    {
        var (output, _) = RunWsl("uname -r", 10000);
        return output.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 一次性配置 Alpine 发行版：免密 sudo、升级仓库到 edge、安装文件系统工具。
    /// <para>One-time Alpine configuration: passwordless sudo, upgrade repos to edge,
    /// install filesystem tools.</para>
    /// </summary>
    public static void EnsureAlpineConfigured()
    {
        if (_alpineConfigured) return;
        lock (ConfigLock)
        {
            if (_alpineConfigured) return;

            // 配置免密 sudo
            RunWsl("echo 'ALL ALL=(ALL) NOPASSWD:ALL' | sudo tee /etc/sudoers.d/nopasswd >/dev/null && sudo chmod 0440 /etc/sudoers.d/nopasswd", 10000);

            // 升级到 edge 仓库（CERNET 镜像）以获取 erofs-utils
            RunWsl("echo 'https://mirrors.cernet.edu.cn/alpine/edge/main' | sudo tee /etc/apk/repositories >/dev/null && echo 'https://mirrors.cernet.edu.cn/alpine/edge/community' | sudo tee -a /etc/apk/repositories >/dev/null && sudo apk update --quiet 2>/dev/null", 60000);

            // 安装文件系统工具
            RunWsl("sudo apk add --no-cache --quiet e2fsprogs-extra erofs-utils f2fs-tools 2>/dev/null", 120000);

            _alpineConfigured = true;
        }
    }

    /// <summary>
    /// 补全 erofs/f2fs 内核模块：尝试 insmod 加载已存在于 /lib/modules 的 .ko 文件。
    /// WSL2 默认禁用 modprobe，但 insmod 直接加载可绕过限制。
    /// <para>Supplements erofs/f2fs kernel modules: attempts insmod to load .ko files already
    /// present in /lib/modules. WSL2 disables modprobe by default, but insmod bypasses this.</para>
    /// </summary>
    /// <returns>erofs 和 f2fs 均已在 /proc/filesystems 中可用时返回 true。</returns>
    public static bool EnsureKernelModules()
    {
        if (_kernelModulesLoaded) return true;
        lock (ConfigLock)
        {
            if (_kernelModulesLoaded) return true;

            // 检查是否已可用
            if (KernelSupports("erofs") && KernelSupports("f2fs"))
            {
                _kernelModulesLoaded = true;
                return true;
            }

            // insmod 加载 erofs
            if (!KernelSupports("erofs"))
            {
                var kernelVer = RunWsl("uname -r", 5000).Output.Trim();
                RunWsl($"sudo insmod /lib/modules/{kernelVer}/kernel/fs/erofs/erofs.ko", 10000);
            }

            // insmod 加载 f2fs
            if (!KernelSupports("f2fs"))
            {
                var kernelVer = RunWsl("uname -r", 5000).Output.Trim();
                RunWsl($"sudo insmod /lib/modules/{kernelVer}/kernel/fs/f2fs/f2fs.ko", 10000);
            }

            _kernelModulesLoaded = KernelSupports("erofs") && KernelSupports("f2fs");
            return _kernelModulesLoaded;
        }
    }

    /// <summary>
    /// 在 WSL Alpine 中执行命令，返回 stdout+stderr 与退出码。
    /// <para>Runs a command inside WSL Alpine and returns stdout+stderr with the exit code.</para>
    /// </summary>
    public static (string Output, int ExitCode) RunWsl(string command, int timeoutMs = 30000)
    {
        // 使用 sh -c 包裹命令，避免 Git Bash 对路径的 mangle（如 /etc → C:/Program Files/Git/etc）
        var escaped = command.Replace("\"", "\\\"");
        var psi = new ProcessStartInfo("wsl", $"-d {DistroName} -- sh -c \"{escaped}\"")
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
    public static string ToWslPath(string windowsPath)
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

    /// <summary>
    /// 创建临时目录。
    /// <para>Creates a temporary directory.</para>
    /// </summary>
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-wsl-" + Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 递归删除目录。
    /// <para>Deletes a directory recursively.</para>
    /// </summary>
    public static void DeleteRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// 探测 WSL Alpine 是否支持 loop 挂载：真实创建一个小 ext4 镜像并尝试挂载（结果缓存）。
    /// <para>Probes whether WSL Alpine supports loop mounts by actually creating and mounting a small
    /// ext4 image (cached).</para>
    /// </summary>
    private static bool? _loopMountProbe;

    public static bool CanLoopMount()
    {
        if (_loopMountProbe.HasValue) return _loopMountProbe.Value;

        var script =
            "td=$(mktemp -d) && " +
            "sudo truncate -s 8M \"$td/t.img\" && " +
            "sudo mke2fs -q -t ext4 \"$td/t.img\" >/dev/null 2>&1 && " +
            "mkdir -p \"$td/mnt\" && " +
            "if sudo mount -o loop \"$td/t.img\" \"$td/mnt\" >/dev/null 2>&1; then " +
            "  sudo umount \"$td/mnt\" >/dev/null 2>&1; rm -rf \"$td\"; echo LOOP_OK; " +
            "else rm -rf \"$td\"; echo LOOP_FAIL; fi";
        var (output, exitCode) = RunWsl(script, 30000);
        _loopMountProbe = exitCode == 0 && output.Contains("LOOP_OK", StringComparison.Ordinal);
        return _loopMountProbe.Value;
    }

    /// <summary>
    /// 探测内核是否支持指定文件系统（/proc/filesystems 含目标类型）。
    /// <para>Probes whether the kernel supports the given filesystem (present in /proc/filesystems).</para>
    /// </summary>
    public static bool KernelSupports(string fsType)
    {
        var (output, _) = RunWsl($"cat /proc/filesystems 2>/dev/null | grep -w {fsType}", 10000);
        return output.Contains(fsType, StringComparison.Ordinal);
    }

    /// <summary>
    /// 工具可用性缓存：tool → 是否存在。线程安全，仅探测一次。
    /// <para>Tool availability cache: tool → present. Thread-safe, probed once.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> s_toolCache = new();

    /// <summary>
    /// 探测的工具集合。
    /// <para>The set of probed tools.</para>
    /// </summary>
    private static readonly string[] s_probedTools =
    [
        "mke2fs", "tune2fs", "dumpe2fs", "debugfs", "e2fsck", "resize2fs",
        "mkfs.erofs", "fsck.erofs", "dump.erofs",
        "mkfs.f2fs", "fsck.f2fs", "dump.f2fs"
    ];

    /// <summary>
    /// 批量探测 WSL Alpine 中各工具是否存在。
    /// <para>Batch-probes whether each tool exists in WSL Alpine.</para>
    /// </summary>
    private static void EnsureToolsProbed()
    {
        if (!s_toolCache.IsEmpty) return;

        var probe = string.Join("; ", s_probedTools.Select(t => $"command -v {t}"));
        var (output, _) = RunWsl(probe, 15000);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < s_probedTools.Length; i++)
        {
            var tool = s_probedTools[i];
            var line = i < lines.Length ? lines[i].Trim() : string.Empty;
            var present = line.Length > 0 && !line.Contains("not found", StringComparison.OrdinalIgnoreCase);
            s_toolCache[tool] = present;
        }
    }

    /// <summary>
    /// 查询某工具是否在 WSL Alpine 中可用。
    /// <para>Queries whether a tool is available in WSL Alpine.</para>
    /// </summary>
    public static bool IsToolAvailable(string tool)
    {
        EnsureToolsProbed();
        return s_toolCache.TryGetValue(tool, out var present) && present;
    }

    private static bool s_avbToolProvisioned;

    /// <summary>
    /// 把仓库内固定的 avbtool.py（TestInfrastructure/avbtool.py，AOSP avbtool 1.2.0 副本）
    /// 安装进 WSL Alpine（/usr/local/bin/avbtool），确保 python3 存在。失败时返回 false，
    /// 由调用方以 Assert.Skip 跳过。工具缺失的 Alpine 仓库不提供 avbtool 包。
    /// <para>Installs the repo-pinned avbtool.py (TestInfrastructure/avbtool.py, a copy of
    /// AOSP avbtool 1.2.0) into WSL Alpine (/usr/local/bin/avbtool), ensuring python3 exists.
    /// Returns false on failure so callers can Assert.Skip; Alpine repos do not package avbtool.</para>
    /// </summary>
    public static bool EnsureAvbTool()
    {
        if (s_avbToolProvisioned) return true;

        RunWsl("command -v python3 >/dev/null || sudo apk add --no-cache --quiet python3", 180000);
        // avbtool 1.2 经 openssl 命令行执行 RSA 运算。
        // avbtool 1.2 shells out to the openssl CLI for RSA operations.
        RunWsl("command -v openssl >/dev/null || sudo apk add --no-cache --quiet openssl", 180000);
        RunWsl("sudo mkdir -p /usr/local/bin", 10000);
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "avbtool.py");
        if (!File.Exists(sourcePath))
        {
            return false;
        }
        // shebang 统一指向 python3（上游为 python3，防御性重写以兼容 PATH 差异）。
        // Normalize the shebang to python3 defensively (upstream already uses python3).
        RunWsl($"sudo sh -c \"sed '1s|.*|#!/usr/bin/env python3|' '{ToWslPath(sourcePath)}' > /usr/local/bin/avbtool && chmod +x /usr/local/bin/avbtool\"", 30000);
        var (output, exitCode) = RunWsl("avbtool version", 30000);
        s_avbToolProvisioned = exitCode == 0 && output.Contains("avbtool", StringComparison.Ordinal);
        if (s_avbToolProvisioned)
        {
            s_toolCache["avbtool"] = true;
        }
        return s_avbToolProvisioned;
    }

    /// <summary>
    /// 挂载失败的分类结果。
    /// <para>Classification of mount failure reasons.</para>
    /// </summary>
    public enum MountFailureKind
    {
        /// <summary>挂载成功或未尝试。</summary>
        None,

        /// <summary>内核模块版本与 mkfs 工具版本不兼容（Bad message / Invalid argument）。</summary>
        VersionMismatch,

        /// <summary>其他挂载失败。</summary>
        Other
    }

    /// <summary>
    /// 在 WSL Alpine 内挂载镜像并验证文件读回，随后卸载。
    /// <para>Mounts the image inside WSL Alpine, verifies file read-back, then unmounts.</para>
    /// </summary>
    /// <param name="imageWslPath">镜像的 WSL 路径。The WSL path of the image.</param>
    /// <param name="fsType">文件系统类型。The filesystem type.</param>
    /// <param name="expectedFiles">预期可读回的文件列表。Files expected to be readable.</param>
    /// <returns>(成功?, 失败分类, 输出)。(ok?, failure kind, output).</returns>
    public static (bool Ok, MountFailureKind FailureKind, string Output) MountVerifyAndUnmount(
        string imageWslPath, string fsType, string[] expectedFiles)
    {
        var mountPoint = "/tmp/uotan-mount-check";
        // 使用独立的 if/else 确保 mount 失败时退出码非零
        // Use independent if/else so mount failure yields a non-zero exit code.
        var script = $"rm -rf {mountPoint} && mkdir -p {mountPoint} && ";
        script += $"if sudo mount -t {fsType} -o loop,ro {imageWslPath} {mountPoint} 2>&1; then ";
        foreach (var file in expectedFiles)
        {
            script += $"cat {mountPoint}/{file} 2>&1 && ";
        }

        script += $"sudo umount {mountPoint} 2>&1 && rm -rf {mountPoint} && echo MOUNT_VERIFY_OK; ";
        script += $"else echo MOUNT_VERIFY_FAIL; rm -rf {mountPoint}; fi";
        var (output, exitCode) = RunWsl(script, 60000);

        if (output.Contains("MOUNT_VERIFY_OK", StringComparison.Ordinal))
        {
            return (true, MountFailureKind.None, output);
        }

        // 版本不兼容特征：erofs "Bad message"（EBADMSG）或 f2fs "Invalid argument" / "No error information"
        // Version mismatch signatures: erofs "Bad message" (EBADMSG), f2fs "Invalid argument" /
        // "No error information" (WSL2 kernel module rejects the image format).
        if (output.Contains("Bad message", StringComparison.Ordinal) ||
            output.Contains("Invalid argument", StringComparison.Ordinal) ||
            output.Contains("No error information", StringComparison.Ordinal))
        {
            return (false, MountFailureKind.VersionMismatch, output);
        }

        return (false, MountFailureKind.Other, output);
    }
}