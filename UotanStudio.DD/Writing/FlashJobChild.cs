using System.Text;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Command-line entry of the writing helper: the host starts one instance of the application with this switch,
/// the instance runs the flash job and streams its progress and outcome to the file the host reads.
/// <para>写入助手的命令行入口：宿主以该开关启动一个应用实例，该实例运行烧录任务，并把进度与结果流式写入
/// 宿主读取的文件。</para>
/// </summary>
/// <remarks>
/// The helper never raises a user-visible error of its own: every failure is written to the report file as an
/// <c>error</c> line, so the host can show one consistent message whether the write, the device or the
/// privilege check failed.
/// <para>
/// 助手自身不弹出用户可见错误：任何失败都以 <c>error</c> 行写入报告文件，使宿主无论遇到写入、设备还是权限
/// 检查失败，都能呈现一致的消息。
/// </para>
/// </remarks>
public static class FlashJobChild
{
    /// <summary>
    /// The switch selecting the writing helper.
    /// <para>选择写入助手的开关。</para>
    /// </summary>
    public const string Switch = "--flash-disk-job";

    /// <summary>
    /// The switch carrying the file the helper reads the job from.
    /// <para>携带助手读取任务文件的开关。</para>
    /// </summary>
    public const string JobSwitch = "--flash-job";

    /// <summary>
    /// The switch carrying the file the helper streams progress and the outcome to.
    /// <para>携带助手流式写入进度与结果文件的开关。</para>
    /// </summary>
    public const string OutputSwitch = "--flash-job-output";

    /// <summary>
    /// The exit code of a job that completed and passed verification.
    /// <para>任务完成并通过校验的退出码。</para>
    /// </summary>
    public const int ExitSucceeded = 0;

    /// <summary>
    /// The exit code of a job that failed; the cause is in the report file.
    /// <para>任务失败的退出码；原因在报告文件中。</para>
    /// </summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// The exit code of a helper started with unusable arguments.
    /// <para>以不可用参数启动助手的退出码。</para>
    /// </summary>
    public const int ExitInvalidArguments = 2;

    /// <summary>
    /// Gets whether the arguments select the writing helper.
    /// <para>获取参数是否选择写入助手。</para>
    /// </summary>
    /// <param name="args">The process arguments.<para>进程参数。</para></param>
    /// <returns><see langword="true"/> when the first argument is the helper switch.
    /// <para>首个参数为助手开关时为 <see langword="true"/>。</para></returns>
    public static bool IsRequested(string[] args) => args is [Switch, ..];

    /// <summary>
    /// Runs the flash job named by the arguments and streams its progress to the report file.
    /// <para>运行参数指定的烧录任务，并把进度流式写入报告文件。</para>
    /// </summary>
    /// <param name="args">The process arguments.<para>进程参数。</para></param>
    /// <returns>The helper exit code.<para>助手退出码。</para></returns>
    public static int Run(string[] args)
    {
        var jobPath = FindValue(args, JobSwitch);
        var outputPath = FindValue(args, OutputSwitch);
        if (string.IsNullOrWhiteSpace(jobPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            return ExitInvalidArguments;
        }

        try
        {
            using var report = ReportFile.Create(outputPath);
            FlashJob job;
            try
            {
                job = FlashJobFormat.Read(File.ReadAllText(jobPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                report.WriteLine(FlashProgressFormat.WriteError(ex));
                return ExitInvalidArguments;
            }

            var result = FlashJobRunner
                .RunAsync(job, progress => report.WriteLine(FlashProgressFormat.WriteProgress(progress)))
                .GetAwaiter()
                .GetResult();
            report.WriteLine(FlashProgressFormat.WriteCompleted(
                new FlashResult
                {
                    Targets = job.Drives,
                    ImageBytes = result.ImageBytes,
                    BytesWritten = result.BytesWritten,
                    Elapsed = result.Elapsed,
                    SourceChecksum = result.SourceChecksum,
                }));
            return ExitSucceeded;
        }
        catch (Exception ex)
        {
            TryWriteFailure(outputPath, ex);
            return ExitFailed;
        }
    }

    private static void TryWriteFailure(string outputPath, Exception error)
    {
        try
        {
            using var report = ReportFile.Create(outputPath);
            report.WriteLine(FlashProgressFormat.WriteError(error));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? FindValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed class ReportFile : IDisposable
    {
        private readonly StreamWriter _writer;

        private ReportFile(StreamWriter writer) => _writer = writer;

        internal static ReportFile Create(string path)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new ReportFile(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            });
        }

        internal void WriteLine(string line) => _writer.WriteLine(line);

        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}