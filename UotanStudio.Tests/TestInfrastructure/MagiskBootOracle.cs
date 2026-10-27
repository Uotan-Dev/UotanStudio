using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Locates the reference <c>magiskboot</c> binary named by <c>UOTAN_MAGISKBOOT_ORACLE</c> and runs its
/// <c>unpack</c>/<c>repack</c> commands inside a throwaway working directory, so oracle-comparison tests can
/// contrast the managed boot-image implementation with the reference behaviour without sharing files between
/// runs and without ever assembling a shell command line.
/// <para>通过 <c>UOTAN_MAGISKBOOT_ORACLE</c> 定位 <c>magiskboot</c> 参考可执行文件，并在一次性工作目录中运行其
/// <c>unpack</c>/<c>repack</c> 命令，使 oracle 对比测试能够在不共享文件、不拼接 shell 命令行的前提下，
/// 把自有 boot image 实现与参考行为进行对照。</para>
/// </summary>
public static class MagiskBootOracle
{
    /// <summary>Environment variable naming either the magiskboot binary itself or a directory that contains it.
    /// <para>指定 magiskboot 可执行文件本身，或包含该可执行文件的目录的环境变量名。</para></summary>
    public const string ExecutableVariable = "UOTAN_MAGISKBOOT_ORACLE";

    /// <summary>Default wall-clock budget for a single oracle invocation.
    /// <para>单次 oracle 调用的默认挂钟时限。</para></summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private const int MaxCapturedCharacters = 64 * 1024;
    private const int CaptureChunkCharacters = 4096;

    /// <summary>Whether the configured oracle binary exists and is startable on the current operating system.
    /// Gates report an unstartable architecture as unavailable so the dependent test skips instead of failing.
    /// <para>配置指向的 oracle 可执行文件是否存在且能在当前操作系统上启动。门控把无法启动的架构报告为不可用，
    /// 使相关测试跳过而不是失败。</para></summary>
    public static bool IsAvailable => ResolveConfiguredPath(Environment.GetEnvironmentVariable(ExecutableVariable))
        is { } path && IsRunnable(path);

    /// <summary>The resolved magiskboot path named by <c>UOTAN_MAGISKBOOT_ORACLE</c>, or <see langword="null"/>
    /// when the variable is unset or points at nothing readable. A non-<c>null</c> answer does not prove the
    /// binary can be executed by this operating system; use <see cref="IsAvailable"/> for gating.
    /// <para><c>UOTAN_MAGISKBOOT_ORACLE</c> 解析出的 magiskboot 路径；变量未设置或指向不存在的文件时返回
    /// <see langword="null"/>。非空结果并不表示当前操作系统能够执行该文件，门控请使用 <see cref="IsAvailable"/>。</para></summary>
    public static string? TryGetExecutablePath() =>
        ResolveConfiguredPath(Environment.GetEnvironmentVariable(ExecutableVariable));

    /// <summary>Resolves a candidate oracle value, which may be a direct binary path or a directory containing
    /// a <c>magiskboot</c> (or <c>magiskboot.exe</c> on Windows) entry.
    /// <para>解析 oracle 的候选取值：既可以是可执行文件的直接路径，也可以是包含 <c>magiskboot</c>
    /// （Windows 下为 <c>magiskboot.exe</c>）的目录。</para></summary>
    public static string? ResolveConfiguredPath(string? configuredValue)
    {
        var candidate = configuredValue?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (!Directory.Exists(candidate))
        {
            return null;
        }

        var conventional = Path.Combine(candidate, OperatingSystem.IsWindows() ? "magiskboot.exe" : "magiskboot");
        if (File.Exists(conventional))
        {
            return conventional;
        }

        return Directory.EnumerateFiles(candidate, OperatingSystem.IsWindows() ? "magiskboot*.exe" : "magiskboot*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Creates an empty, isolated working directory for one oracle run; disposing removes it.
    /// <para>为一次 oracle 运行创建空的隔离工作目录；释放时删除。</para></summary>
    public static MagiskBootWorkspace CreateWorkspace() => MagiskBootWorkspace.Create();

    /// <summary>Runs <c>magiskboot unpack</c> for <paramref name="imagePath"/> after staging the image into
    /// <paramref name="workingDirectory"/>, so the reference binary only ever sees a relative file name.
    /// <para>先把 <paramref name="imagePath"/> 复制进 <paramref name="workingDirectory"/>，再运行
    /// <c>magiskboot unpack</c>，使参考二进制只看到相对文件名。</para></summary>
    public static Task<MagiskBootRunResult> UnpackAsync(
        string imagePath,
        string workingDirectory,
        IReadOnlyList<string>? options = null,
        CancellationToken cancellationToken = default) =>
        RunFileCommandAsync("unpack", imagePath, workingDirectory, options, outputName: null, cancellationToken);

    /// <summary>Runs <c>magiskboot repack</c> inside <paramref name="workingDirectory"/>; when
    /// <paramref name="outputName"/> is supplied the reference binary writes that name instead of replacing the
    /// input image, keeping the staged input intact for byte comparison.
    /// <para>在 <paramref name="workingDirectory"/> 内运行 <c>magiskboot repack</c>；提供
    /// <paramref name="outputName"/> 时参考二进制写入该文件名而不是替换输入镜像，以便保留输入做字节比对。</para></summary>
    public static Task<MagiskBootRunResult> RepackAsync(
        string imagePath,
        string workingDirectory,
        IReadOnlyList<string>? options = null,
        string? outputName = null,
        CancellationToken cancellationToken = default) =>
        RunFileCommandAsync("repack", imagePath, workingDirectory, options, outputName, cancellationToken);

    /// <summary>Runs the oracle binary with an explicit argument list in <paramref name="workingDirectory"/> and
    /// captures bounded stdout/stderr. The call never uses a shell, so paths stay data rather than command text.
    /// <para>在 <paramref name="workingDirectory"/> 中用显式参数列表运行 oracle 二进制，并捕获有界的
    /// stdout/stderr。调用不经过 shell，因此路径始终是数据而不是命令文本。</para></summary>
    /// <param name="arguments">Arguments passed verbatim through the process argument list.
    /// 通过进程参数列表原样传递的参数。</param>
    /// <param name="workingDirectory">Directory the oracle runs in; created when missing.
    /// oracle 运行的目录，不存在时创建。</param>
    /// <param name="timeout">Wall-clock budget before the process tree is killed; defaults to
    /// <see cref="DefaultTimeout"/>. 进程树被终止前的挂钟时限，默认取 <see cref="DefaultTimeout"/>。</param>
    /// <param name="cancellationToken">Cancels the invocation and propagates
    /// <see cref="OperationCanceledException"/>; a timeout instead yields
    /// <see cref="MagiskBootRunResult.TimedOut"/>. 取消调用并传播
    /// <see cref="OperationCanceledException"/>；超时则只置位
    /// <see cref="MagiskBootRunResult.TimedOut"/>。</param>
    /// <returns>Exit code plus captured streams. 退出码与捕获的输出流。</returns>
    public static async Task<MagiskBootRunResult> RunAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var executable = ResolveConfiguredPath(Environment.GetEnvironmentVariable(ExecutableVariable))
            ?? throw new InvalidOperationException(
                $"The magiskboot oracle is missing: set {ExecutableVariable} to a magiskboot binary or to a " +
                "directory containing one.");
        Directory.CreateDirectory(workingDirectory);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            return new MagiskBootRunResult(false, -1, string.Empty,
                $"{executable} could not be started: {exception.Message}", TimedOut: false);
        }

        using var timeoutGate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutGate.CancelAfter(timeout ?? DefaultTimeout);
        var timedOut = false;
        var standardOutput = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var standardError = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            await Task.WhenAll(standardOutput, standardError, process.WaitForExitAsync(timeoutGate.Token))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            Terminate(process);
            await Task.WhenAll(standardOutput, standardError,
                process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Terminate(process);
            throw;
        }

        return new MagiskBootRunResult(true, process.ExitCode,
            await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false), timedOut);
    }

    private static async Task<MagiskBootRunResult> RunFileCommandAsync(
        string command,
        string imagePath,
        string workingDirectory,
        IReadOnlyList<string>? options,
        string? outputName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Directory.CreateDirectory(workingDirectory);
        var imageName = Path.GetFileName(imagePath);
        var stagedPath = Path.GetFullPath(Path.Combine(workingDirectory, imageName));
        if (!string.Equals(Path.GetFullPath(imagePath), stagedPath, StringComparison.Ordinal))
        {
            File.Copy(imagePath, stagedPath, overwrite: true);
        }

        var arguments = new List<string> { command };
        if (options is not null)
        {
            arguments.AddRange(options);
        }

        if (outputName is not null)
        {
            arguments.Add("-o");
            arguments.Add(Path.GetFileName(outputName));
        }

        arguments.Add(imageName);
        return await RunAsync(arguments, workingDirectory, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var captured = new StringBuilder();
        var chunk = new char[CaptureChunkCharacters];
        var exceeded = false;
        while (true)
        {
            var read = await reader.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var room = MaxCapturedCharacters - captured.Length;
            if (room <= 0)
            {
                exceeded = true;
                continue;
            }

            var take = Math.Min(read, room);
            captured.Append(chunk, 0, take);
            exceeded |= take < read;
        }

        return exceeded ? captured.Append("…[truncated]").ToString() : captured.ToString();
    }

    private static bool IsRunnable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Result of one magiskboot oracle invocation: whether the process could start at all, its exit code, the
/// bounded captured streams, and whether the wall-clock budget expired before the process finished.
/// <para>单次 magiskboot oracle 调用的结果：进程能否启动、退出码、有界捕获的输出，以及挂钟时限是否在进程结束前耗尽。</para>
/// </summary>
/// <param name="Launched">Whether the oracle process started. oracle 进程是否成功启动。</param>
/// <param name="ExitCode">Process exit code, or <c>-1</c> when the process never started.
/// 进程退出码；进程未能启动时为 <c>-1</c>。</param>
/// <param name="StandardOutput">Captured stdout, truncated past the internal bound.
/// 捕获的 stdout，超过内部上限时截断。</param>
/// <param name="StandardError">Captured stderr, truncated past the internal bound.
/// 捕获的 stderr，超过内部上限时截断。</param>
/// <param name="TimedOut">Whether the invocation was killed after the timeout. 调用是否因超时被终止。</param>
public sealed record MagiskBootRunResult(
    bool Launched,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    /// <summary>Captured streams joined in read order, for assertion failure messages.
    /// 按读取顺序拼接的捕获输出，用于断言失败信息。</summary>
    public string CombinedOutput => StandardOutput + StandardError;
}

/// <summary>
/// Disposable working directory for a single magiskboot oracle run; magiskboot writes its <c>kernel</c>,
/// <c>ramdisk.cpio*</c> and <c>dtb</c> siblings next to the staged image, so every run needs its own folder.
/// <para>单次 magiskboot oracle 运行的可释放工作目录；magiskboot 会在暂存镜像旁写入 <c>kernel</c>、
/// <c>ramdisk.cpio*</c> 与 <c>dtb</c> 等兄弟文件，因此每次运行都需要独立目录。</para>
/// </summary>
public sealed class MagiskBootWorkspace : IDisposable
{
    private MagiskBootWorkspace(string directoryPath) => DirectoryPath = directoryPath;

    /// <summary>Absolute path of the throwaway directory. 一次性目录的绝对路径。
    /// <para>一次性目录的绝对路径。</para></summary>
    public string DirectoryPath { get; }

    /// <summary>Absolute path for a name relative to the working directory.
    /// <para>相对工作目录名称的绝对路径。</para></summary>
    public string PathFor(string relativeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeName);
        return Path.Combine(DirectoryPath, relativeName);
    }

    /// <summary>Copies <paramref name="sourcePath"/> into the working directory and returns the bare file name to
    /// hand the oracle. 把 <paramref name="sourcePath"/> 复制进工作目录，并返回应传给 oracle 的裸文件名。</summary>
    public string Stage(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var name = Path.GetFileName(sourcePath);
        File.Copy(sourcePath, PathFor(name), overwrite: true);
        return name;
    }

    internal static MagiskBootWorkspace Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-magiskboot-oracle-", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new MagiskBootWorkspace(path);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}