using System.Diagnostics;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Native;

/// <summary>Runs installed filesystem tools without a shell; Windows callers may select a WSL distribution.
/// <para>以无 shell 方式运行已安装的文件系统工具；Windows 调用方可选择 WSL 发行版。</para></summary>
/// <param name="wslDistribution">
/// WSL distribution name passed to <c>wsl.exe -d</c> on Windows and ignored on other platforms. When it is <see langword="null"/> or whitespace on Windows, every tool invocation throws <see cref="PlatformNotSupportedException"/>.
/// <para>Windows 下传给 <c>wsl.exe -d</c> 的 WSL 发行版名称，其他平台忽略。Windows 下为 <see langword="null"/> 或空白时，每次工具调用都会抛出 <see cref="PlatformNotSupportedException"/>。</para>
/// </param>
public sealed class NativeFileSystemTools(string? wslDistribution = null)
{
    private const int MaximumOutputChars = 16 * 1024;

    /// <summary>
    /// Translates a host path into the form the native tools see: plain full-path resolution outside Windows, a blocking <c>wslpath</c> invocation inside the configured WSL distribution on Windows.
    /// <para>将主机路径转换为本机工具可见的形式：非 Windows 平台直接解析完整路径；Windows 在已配置的 WSL 发行版内阻塞调用 <c>wslpath</c>。</para>
    /// </summary>
    /// <param name="path">Host path resolved with <c>Path.GetFullPath</c> before translation.
    /// <para>转换前先经 <c>Path.GetFullPath</c> 解析的主机路径。</para></param>
    /// <param name="cancellationToken">Cancels the <c>wslpath</c> process and kills its tree.
    /// <para>取消 <c>wslpath</c> 进程并终止其进程树。</para></param>
    /// <returns>The absolute path as seen by tools running in the WSL distribution.
    /// <para>在 WSL 发行版中运行的工具所见的绝对路径。</para></returns>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="InvalidDataException"><c>wslpath</c> exited with a non-zero code.
    /// <para><c>wslpath</c> 以非零退出码结束。</para></exception>
    /// <exception cref="IOException">The process could not start, or <c>wslpath</c> returned a path that does not begin with <c>/</c>.
    /// <para>无法启动进程，或 <c>wslpath</c> 返回了不以 <c>/</c> 开头的路径。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public string PathForTool(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        if (string.IsNullOrWhiteSpace(wslDistribution))
        {
            throw new PlatformNotSupportedException("Native filesystem tools on Windows require an explicit WSL distribution.");
        }

        var result = RunAsync("wslpath", [fullPath.Replace('\\', '/')], cancellationToken).GetAwaiter().GetResult();
        RequireSuccess("wslpath", result);
        var translated = result.Output.Trim();
        if (!translated.StartsWith('/'))
        {
            throw new IOException("WSL did not return an absolute path.");
        }

        return translated;
    }

    /// <summary>
    /// Starts one tool process without a shell, merging stdout and stderr into a single capture bounded at 16384 characters. On Windows the tool runs through <c>wsl.exe -d &lt;distribution&gt; -- &lt;tool&gt;</c>; elsewhere it is started directly. Cancellation kills the whole process tree and waits for the process to exit before rethrowing.
    /// <para>以无 shell 方式启动一个工具进程，并把 stdout 与 stderr 合并捕获、上限 16384 字符。Windows 下工具通过 <c>wsl.exe -d &lt;发行版&gt; -- &lt;工具&gt;</c> 运行；其他平台直接启动。取消时终止整个进程树，并等待进程退出后再重新抛出。</para>
    /// </summary>
    /// <param name="tool">Executable name started directly on Linux, or the command run inside the WSL distribution on Windows.
    /// <para>Linux 下直接启动的可执行文件名；Windows 下为在 WSL 发行版内执行的命令。</para></param>
    /// <param name="arguments">Argument strings passed verbatim, one list entry per argument, never re-parsed by a shell.
    /// <para>逐字传递的参数列表，每项一个参数，绝不经过 shell 重新解析。</para></param>
    /// <param name="cancellationToken">Cancels the run; the process tree is killed and awaited before the exception surfaces.
    /// <para>取消运行；先终止并等待进程树退出，异常才会抛出。</para></param>
    /// <returns>A task carrying the exit code and the bounded merged output.
    /// <para>携带退出码与有界合并输出的任务。</para></returns>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="IOException">The process could not be started.
    /// <para>无法启动进程。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled during startup, output capture, or exit wait.
    /// <para>在启动、输出捕获或等待退出期间令牌被取消。</para></exception>
    public async Task<NativeToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(wslDistribution))
        {
            throw new PlatformNotSupportedException("Native filesystem tools on Windows require an explicit WSL distribution.");
        }

        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "wsl.exe" : tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add(wslDistribution!);
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(tool);
        }
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new IOException($"Could not start {tool}.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = new StringBuilder();
        var stdout = CaptureAsync(process.StandardOutput, output, cancellationToken);
        var stderr = CaptureAsync(process.StandardError, output, cancellationToken);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new NativeToolResult(process.ExitCode, output.ToString());
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Throws when a tool exited with a non-zero code; a zero exit code returns silently.
    /// <para>工具以非零退出码结束时抛出异常；退出码为 <c>0</c> 时静默返回。</para>
    /// </summary>
    /// <param name="tool">Tool name embedded in the failure message.
    /// <para>嵌入失败信息的工具名。</para></param>
    /// <param name="result">Result whose <see cref="NativeToolResult.ExitCode"/> is tested.
    /// <para>被测 <see cref="NativeToolResult.ExitCode"/> 的运行结果。</para></param>
    /// <exception cref="InvalidDataException">The exit code is non-zero; the message embeds the captured output.
    /// <para>退出码非零；消息内嵌已捕获的输出。</para></exception>
    public static void RequireSuccess(string tool, NativeToolResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"{tool} exited with code {result.ExitCode}: {result.Output}");
        }
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder output, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            lock (output)
            {
                if (output.Length < MaximumOutputChars)
                {
                    output.Append(buffer, 0, Math.Min(count, MaximumOutputChars - output.Length));
                }
            }
        }
    }
}

/// <summary>Outcome of one native tool run: the process exit code plus the bounded merged output.
/// <para>一次本机工具运行的结果：进程退出码与有界合并输出。</para></summary>
/// <param name="ExitCode">Process exit code; <c>0</c> means success.
/// <para>进程退出码；<c>0</c> 表示成功。</para></param>
/// <param name="Output">stdout and stderr merged into one string, truncated at 16384 characters.
/// <para>stdout 与 stderr 的合并字符串，截断于 16384 字符。</para></param>
public readonly record struct NativeToolResult(int ExitCode, string Output);