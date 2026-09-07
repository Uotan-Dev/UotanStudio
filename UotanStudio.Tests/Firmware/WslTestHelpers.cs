using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Runs tests against existing WSL tools without provisioning dependencies.
/// <para>使用现有 WSL 工具运行测试，不自动准备依赖。</para>
/// </summary>
internal static class WslTestHelpers
{
    /// <summary>
    /// Alpine WSL2 发行版名称。
    /// <para>The Alpine WSL2 distribution name.</para>
    /// </summary>
    public static string DistroName { get; } = Environment.GetEnvironmentVariable("UOTAN_TEST_WSL_DISTRO") ?? "Alpine";

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
            if (!proc.WaitForExit(5000)) { proc.Kill(entireProcessTree: true); proc.WaitForExit(); return false; }
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
        try { return RunWsl("true", 15000).ExitCode == 0; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>
    /// 当前 WSL 是否为 WSL2 内核（含 "WSL2" 标记）。
    /// <para>Whether the current WSL runs a WSL2 kernel (contains the "WSL2" tag).</para>
    /// </summary>
    public static bool IsWsl2()
    {
        var (output, _) = RunWsl("uname -r", 10000);
        return output.Contains("WSL2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在 WSL Alpine 中执行命令，返回 stdout+stderr 与退出码。
    /// <para>Runs a command inside WSL Alpine and returns stdout+stderr with the exit code.</para>
    /// </summary>
    public static (string Output, int ExitCode) RunWsl(string command, int timeoutMs = 30000, bool asRoot = false)
    {
        return RunWslProcess(["--exec", "sh", "-s"], timeoutMs, command, asRoot);
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

    private static readonly ConcurrentDictionary<string, bool> s_toolCache = new();

    /// <summary>Checks each tool independently using its exit status. <para>按退出状态独立检查各工具。</para></summary>
    public static bool IsToolAvailable(string tool)
        => s_toolCache.GetOrAdd(tool, name => RunWsl($"command -v {Quote(name)} >/dev/null 2>&1", 15000).ExitCode == 0);

    /// <summary>Quotes a literal shell argument. <para>转义 shell 字面参数。</para></summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static bool s_avbToolProvisioned;

    /// <summary>
    /// 验证随测试复制的固定 avbtool 1.3.0 与 WSL 依赖，不修改系统配置。
    /// 仅缺少 Python/OpenSSL 时返回跳过原因，夹具损坏或工具运行错误直接抛出。
    /// <para>Validates the bundled avbtool 1.3.0 and WSL dependencies without changing system configuration.
    /// Returns a skip reason only for missing Python/OpenSSL; invalid fixtures or tool failures throw.</para>
    /// </summary>
    public static bool EnsureAvbTool(out string reason)
    {
        lock (ConfigLock)
        {
            reason = string.Empty;
            if (s_avbToolProvisioned) return true;
            using (var script = File.OpenRead(AvbToolPath))
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(script));
                if (hash != "fc5332131a907ae22978e16a84fa12a14d277a60d8e2ad77c31a0097dafeb657")
                    throw new InvalidDataException("The bundled avbtool.py does not match its pinned upstream SHA-256.");
            }
            foreach (var (program, argument) in new[] { ("python3", "--version"), ("openssl", "version") })
            {
                var (output, exitCode) = RunWslExecutable(program, [argument], 30000);
                if (exitCode != 0)
                {
                    reason = $"WSL distribution '{DistroName}' dependency '{program}' is unavailable (exit {exitCode}): {output.Trim()}.";
                    return false;
                }
            }
            var version = RunAvbTool(["version"], 30000);
            if (version.ExitCode != 0 || version.Output.Trim() != "avbtool 1.3.0")
                throw new InvalidOperationException($"The bundled avbtool version check failed (exit {version.ExitCode}): {version.Output}");
            s_avbToolProvisioned = true;
            return true;
        }
    }

    private static string AvbToolPath => Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "avbtool.py");

    /// <summary>
    /// 通过 Python 直接运行固定工具，独立传递参数以保留含空格的路径。
    /// <para>Runs the pinned tool directly through Python, preserving spaced paths as separate arguments.</para>
    /// </summary>
    public static (string Output, int ExitCode) RunAvbTool(IReadOnlyList<string> arguments, int timeoutMs = 120000)
        => RunWslExecutable("python3", [ToWslPath(AvbToolPath), .. arguments], timeoutMs);

    /// <summary>Runs an executable with separate arguments. <para>以独立参数运行程序。</para></summary>
    public static (string Output, int ExitCode) RunWslExecutable(string program, IReadOnlyList<string> arguments, int timeoutMs = 30000, bool asRoot = false)
        => RunWslProcess(["--exec", program, .. arguments], timeoutMs, asRoot: asRoot);

    private static (string Output, int ExitCode) RunWslProcess(IReadOnlyList<string> arguments, int timeoutMs, string? script = null, bool asRoot = false)
    {
        var start = new ProcessStartInfo("wsl")
        {
            UseShellExecute = false,
            RedirectStandardInput = script is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add(DistroName);
        if (asRoot)
        {
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add("root");
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Could not start WSL."); }
        catch (System.ComponentModel.Win32Exception ex) { return (ex.Message, -1); }
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var input = script is null ? Task.CompletedTask : WriteScriptAsync(process, script);
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException($"WSL exceeded {timeoutMs} ms.");
            }
            try { input.GetAwaiter().GetResult(); }
            catch (IOException) when (process.ExitCode != 0) { /* Preserve the distribution startup error. */ }
            return (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult(), process.ExitCode);
        }
    }

    private static async Task WriteScriptAsync(Process process, string script)
    {
        await process.StandardInput.WriteAsync(script.Replace("\r\n", "\n") + "\n");
        process.StandardInput.Close();
    }
}