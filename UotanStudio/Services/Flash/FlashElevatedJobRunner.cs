using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using UotanStudio.DD;
using UotanStudio.DD.Writing;
using DiskFlashProgress = UotanStudio.DD.FlashProgress;

namespace UotanStudio.Services.Flash;

/// <summary>
/// How a flash job started on behalf of the host finished.
/// <para>代表宿主启动的烧录任务的结束方式。</para>
/// </summary>
public enum FlashJobRunStatus
{
    /// <summary>The job wrote and verified every target.
    /// <para>任务已写入并校验全部目标。</para></summary>
    Succeeded,

    /// <summary>The writing process could not open a target because it lacks the privilege.
    /// <para>写入进程因缺少权限而无法打开目标。</para></summary>
    AccessDenied,

    /// <summary>The job failed after the write privilege was available.
    /// <para>写权限已具备后任务失败。</para></summary>
    Failed,

    /// <summary>The caller cancelled the job.<para>调用方取消了任务。</para></summary>
    Cancelled,

    /// <summary>This platform has no way to start a writing process with more privilege.
    /// <para>该平台无法以更高权限启动写入进程。</para></summary>
    ElevationUnavailable,

    /// <summary>The user dismissed the elevation prompt.<para>用户关闭了提权提示。</para></summary>
    ElevationCancelled,
}

/// <summary>
/// Outcome of a flash job the host delegated to a writing process.
/// <para>宿主委托给写入进程的烧录任务结果。</para>
/// </summary>
public sealed record FlashJobRunResult
{
    /// <summary>
    /// Gets how the job finished.<para>获取任务的结束方式。</para>
    /// </summary>
    public required FlashJobRunStatus Status { get; init; }

    /// <summary>
    /// Gets the completed outcome; set only when <see cref="Status"/> is <see cref="FlashJobRunStatus.Succeeded"/>.
    /// <para>获取完成后的结果；仅当 <see cref="Status"/> 为 <see cref="FlashJobRunStatus.Succeeded"/> 时有值。</para>
    /// </summary>
    public FlashJobResult? Result { get; init; }

    /// <summary>
    /// Gets the failure text the writing process reported, or a host-side explanation.
    /// <para>获取写入进程上报的失败文本，或宿主侧的解释。</para>
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Runs one flash job in a separate process started with the write privilege, so the host itself never needs
/// an administrator token and an unelevated host can still finish the write after one prompt.
/// <para>在带写权限的独立进程中运行烧录任务，使宿主自身无需管理员令牌，未提权的宿主也能在确认一次后完成写入。</para>
/// </summary>
/// <remarks>
/// The host writes the job to a temporary file, starts the application with the <see cref="FlashJobChild"/>
/// switch through <c>runas</c>, and tails the report file the child streams. Progress, the outcome and the
/// failure kind therefore cross the process boundary as text, and a cancelled job is killed with its tree.
/// <para>
/// 宿主把任务写入临时文件，经 <c>runas</c> 以 <see cref="FlashJobChild"/> 开关启动应用，并持续读取子进程流式
/// 写出的报告文件。进度、结果与失败类别因此以文本跨越进程边界；被取消的任务连同子进程树一起终止。
/// </para>
/// </remarks>
public static class FlashElevatedJobRunner
{
    private const int ErrorCancelled = 1223;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Runs the job in an elevated writing process where the platform supports it.
    /// <para>在平台支持时以提权写入进程运行该任务。</para>
    /// </summary>
    /// <param name="job">Job to run.<para>待运行的任务。</para></param>
    /// <param name="onProgress">Callback receiving progress from the writing process.
    /// <para>接收写入进程进度的回调。</para></param>
    /// <param name="cancellationToken">Cancels the job; the writing process is terminated with its tree.
    /// <para>取消任务；写入进程连同其进程树被终止。</para></param>
    /// <returns>How the job finished.<para>任务的结束方式。</para></returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.
    /// <para>必需参数为 <see langword="null"/>。</para></exception>
    public static Task<FlashJobRunResult> RunAsync(
        FlashJob job,
        Action<DiskFlashProgress> onProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(onProgress);

        return OperatingSystem.IsWindows()
            ? RunWindowsAsync(job, onProgress, cancellationToken)
            : Task.FromResult(new FlashJobRunResult
            {
                Status = FlashJobRunStatus.ElevationUnavailable,
                Message = "Raw disk writing runs in an elevated process only on Windows.",
            });
    }

    private static async Task<FlashJobRunResult> RunWindowsAsync(
        FlashJob job,
        Action<DiskFlashProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath) ||
            string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return new FlashJobRunResult
            {
                Status = FlashJobRunStatus.ElevationUnavailable,
                Message = "This host cannot start itself with administrator rights.",
            };
        }

        var directory = Path.Combine(Path.GetTempPath(), "UotanStudio.FlashJob");
        var jobPath = Path.Combine(directory, Path.GetRandomFileName() + ".job");
        var reportPath = Path.Combine(directory, Path.GetRandomFileName() + ".report");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(jobPath, FlashJobFormat.Write(job), cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            startInfo.ArgumentList.Add(FlashJobChild.Switch);
            startInfo.ArgumentList.Add(FlashJobChild.JobSwitch);
            startInfo.ArgumentList.Add(jobPath);
            startInfo.ArgumentList.Add(FlashJobChild.OutputSwitch);
            startInfo.ArgumentList.Add(reportPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new FlashJobRunResult
                {
                    Status = FlashJobRunStatus.Failed,
                    Message = "The writing process could not be started.",
                };
            }

            return await PumpAsync(process, reportPath, onProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new FlashJobRunResult
            {
                Status = FlashJobRunStatus.ElevationCancelled,
                Message = ex.Message,
            };
        }
        catch (OperationCanceledException)
        {
            return new FlashJobRunResult { Status = FlashJobRunStatus.Cancelled };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException)
        {
            return new FlashJobRunResult
            {
                Status = FlashJobRunStatus.Failed,
                Message = ex.Message,
            };
        }
        finally
        {
            TryDelete(jobPath);
            TryDelete(reportPath);
        }
    }

    private static async Task<FlashJobRunResult> PumpAsync(
        Process process,
        string reportPath,
        Action<DiskFlashProgress> onProgress,
        CancellationToken cancellationToken)
    {
        FlashJobResult? completed = null;
        FlashProgressReport? failure = null;
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var pending = new StringBuilder();
        long position = 0;
        using var registration = cancellationToken.Register(() => TryKill(process));
        while (true)
        {
            if (File.Exists(reportPath))
            {
                var read = ReadChunk(reportPath, position, bytes);
                if (read > 0)
                {
                    position += read;
                    var charCount = decoder.GetChars(bytes, 0, read, chars, 0);
                    pending.Append(chars, 0, charCount);
                    DrainLines(pending, line =>
                    {
                        var report = FlashProgressFormat.TryParse(line);
                        if (report is null)
                        {
                            return;
                        }

                        if (report.Progress is { } progress)
                        {
                            onProgress(progress);
                        }
                        else if (report.Result is { } result)
                        {
                            completed = result;
                        }
                        else if (report.IsError)
                        {
                            failure = report;
                        }
                    });
                    continue;
                }
            }

            if (process.HasExited && position >= ReportLength(reportPath))
            {
                break;
            }

            await Task.Delay(PollInterval, CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new FlashJobRunResult { Status = FlashJobRunStatus.Cancelled };
            }
        }

        process.WaitForExit();
        if (completed is not null)
        {
            return new FlashJobRunResult { Status = FlashJobRunStatus.Succeeded, Result = completed };
        }

        if (failure is not null)
        {
            return new FlashJobRunResult
            {
                Status = IsAccessDenied(failure) ? FlashJobRunStatus.AccessDenied : FlashJobRunStatus.Failed,
                Message = failure.ErrorMessage,
            };
        }

        return new FlashJobRunResult
        {
            Status = process.ExitCode == FlashJobChild.ExitSucceeded
                ? FlashJobRunStatus.Failed
                : FlashJobRunStatus.Failed,
            Message = $"The writing process exited with code {process.ExitCode} without reporting a result.",
        };
    }

    private static int ReadChunk(string path, long position, byte[] buffer)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= position)
            {
                return 0;
            }

            stream.Seek(position, SeekOrigin.Begin);
            return stream.Read(buffer, 0, (int)Math.Min(buffer.Length, stream.Length - position));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long ReportLength(string path)
    {
        try
        {
            return new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void DrainLines(StringBuilder pending, Action<string> handle)
    {
        var text = pending.ToString();
        var start = 0;
        while (true)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                break;
            }

            handle(text[start..end].TrimEnd('\r'));
            start = end + 1;
        }

        pending.Clear();
        if (start < text.Length)
        {
            pending.Append(text, start, text.Length - start);
        }
    }

    private static bool IsAccessDenied(FlashProgressReport failure) =>
        string.Equals(failure.ErrorType, nameof(FlashAccessDeniedException), StringComparison.Ordinal)
        || string.Equals(failure.InnermostType, nameof(UnauthorizedAccessException), StringComparison.Ordinal)
        || (string.Equals(failure.InnermostType, nameof(Win32Exception), StringComparison.Ordinal)
            && failure.InnermostMessage is { } message
            && (message.Contains("拒绝访问", StringComparison.Ordinal)
                || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)));

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}