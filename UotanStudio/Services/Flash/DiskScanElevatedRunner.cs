using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Writing;

namespace UotanStudio.Services.Flash;

/// <summary>
/// How a privileged drive scan finished.
/// <para>提权驱动器扫描的结束方式。</para>
/// </summary>
public enum DiskScanStatus
{
    /// <summary>Every drive was enumerated with its full metadata.
    /// <para>已枚举全部驱动器及其完整元数据。</para>
    /// </summary>
    Succeeded,

    /// <summary>The scan ran but some drives reported incomplete metadata.
    /// <para>扫描已运行，但部分驱动器的元数据不完整。</para>
    /// </summary>
    Incomplete,

    /// <summary>The user dismissed the elevation prompt.
    /// <para>用户关闭了提权提示。</para>
    /// </summary>
    ElevationCancelled,

    /// <summary>This host cannot start a privileged scan process.
    /// <para>该宿主无法启动提权扫描进程。</para>
    /// </summary>
    ElevationUnavailable,

    /// <summary>The scan process failed; <see cref="DiskScanOutcome.Detail"/> explains why.
    /// <para>扫描进程失败；原因见 <see cref="DiskScanOutcome.Detail"/>。</para>
    /// </summary>
    Failed,
}

/// <summary>
/// Outcome of a privileged drive scan, including the reason when it did not produce full metadata.
/// <para>提权驱动器扫描的结果；未取得完整元数据时包含原因。</para>
/// </summary>
public sealed record DiskScanOutcome
{
    /// <summary>
    /// Gets how the scan finished.<para>获取扫描的结束方式。</para>
    /// </summary>
    public required DiskScanStatus Status { get; init; }

    /// <summary>
    /// Gets the drives the scan resolved, empty when it never ran.
    /// <para>获取扫描解析出的驱动器；未运行过时为空。</para>
    /// </summary>
    public IReadOnlyList<FlashDrive> Drives { get; init; } = [];

    /// <summary>
    /// Gets the unresolved findings, each naming what could not be read.
    /// <para>获取未解析的问题，每一项点名读取失败的内容。</para>
    /// </summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>
    /// Gets the failure text when the scan could not run at all.
    /// <para>获取扫描完全未能运行时的失败文本。</para>
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Runs the drive scan in a separate process that holds the privilege an ordinary host token is denied, so the
/// window can be opened unelevated and still show complete drive metadata after one prompt.
/// <para>在持有普通宿主令牌所缺权限的独立进程中运行驱动器扫描，使窗口可以以普通权限打开，并在确认一次后仍
/// 显示完整的驱动器元数据。</para>
/// </summary>
public static class DiskScanElevatedRunner
{
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Runs the privileged scan where the platform supports it.
    /// <para>在平台支持时运行提权扫描。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait; the scan process is terminated with its tree.
    /// <para>取消等待；扫描进程连同其进程树被终止。</para></param>
    /// <returns>What the scan resolved and why it may be incomplete.
    /// <para>扫描解析出的内容以及可能不完整的原因。</para></returns>
    public static Task<DiskScanOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        return OperatingSystem.IsWindows()
            ? RunWindowsAsync(cancellationToken)
            : Task.FromResult(new DiskScanOutcome
            {
                Status = DiskScanStatus.ElevationUnavailable,
                Detail = "A privileged drive scan runs in a separate process only on Windows.",
            });
    }

    private static Task<DiskScanOutcome> RunWindowsAsync(CancellationToken cancellationToken)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath) ||
            string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new DiskScanOutcome
            {
                Status = DiskScanStatus.ElevationUnavailable,
                Detail = "This host cannot start itself with administrator rights.",
            });
        }

        var directory = Path.Combine(Path.GetTempPath(), "UotanStudio.DiskScan");
        var reportPath = Path.Combine(directory, Path.GetRandomFileName() + ".scan");
        try
        {
            Directory.CreateDirectory(directory);
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            startInfo.ArgumentList.Add(DiskScanChild.Switch);
            startInfo.ArgumentList.Add(DiskScanChild.OutputSwitch);
            startInfo.ArgumentList.Add(reportPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Task.FromResult(new DiskScanOutcome
                {
                    Status = DiskScanStatus.Failed,
                    Detail = "The scan process could not be started.",
                });
            }

            using var registration = cancellationToken.Register(() => TryKill(process));
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                TryKill(process);
                return Task.FromResult(new DiskScanOutcome
                {
                    Status = DiskScanStatus.Failed,
                    Detail = "The scan process did not finish within two minutes.",
                });
            }

            return Task.FromResult(ReadOutcome(reportPath, process.ExitCode));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return Task.FromResult(new DiskScanOutcome
            {
                Status = DiskScanStatus.ElevationCancelled,
                Detail = "The administrator prompt was cancelled; the drives were not scanned with elevated rights.",
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException)
        {
            return Task.FromResult(new DiskScanOutcome
            {
                Status = DiskScanStatus.Failed,
                Detail = ex.Message,
            });
        }
        finally
        {
            TryDelete(reportPath);
        }
    }

    private static DiskScanOutcome ReadOutcome(string reportPath, int exitCode)
    {
        string text;
        try
        {
            text = File.Exists(reportPath) ? File.ReadAllText(reportPath, Encoding.UTF8) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DiskScanOutcome { Status = DiskScanStatus.Failed, Detail = ex.Message };
        }

        if (text.Length == 0)
        {
            return new DiskScanOutcome
            {
                Status = DiskScanStatus.Failed,
                Detail = $"The scan process exited with code {exitCode} without writing a report.",
            };
        }

        foreach (var line in text.Split('\n'))
        {
            var report = FlashProgressFormat.TryParse(line);
            if (report is { IsError: true })
            {
                return new DiskScanOutcome
                {
                    Status = DiskScanStatus.Failed,
                    Detail = string.IsNullOrEmpty(report.ErrorMessage) ? report.ErrorType : report.ErrorMessage,
                };
            }
        }

        DiskScanReport scan;
        try
        {
            scan = DiskScanFormat.Read(text, out var isComplete);
            if (!isComplete)
            {
                return new DiskScanOutcome
                {
                    Status = DiskScanStatus.Failed,
                    Detail = "The scan process stopped before it finished enumerating the drives.",
                };
            }
        }
        catch (FormatException ex)
        {
            return new DiskScanOutcome { Status = DiskScanStatus.Failed, Detail = ex.Message };
        }

        return new DiskScanOutcome
        {
            Status = scan.Issues.Count == 0 ? DiskScanStatus.Succeeded : DiskScanStatus.Incomplete,
            Drives = scan.Drives,
            Issues = scan.Issues,
        };
    }

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