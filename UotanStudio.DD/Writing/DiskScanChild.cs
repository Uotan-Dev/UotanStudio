using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Command-line entry of the disk scan helper: the host starts one instance of the application with this switch,
/// the instance enumerates the physical drives with its own privilege and writes the report the host reads.
/// <para>磁盘扫描助手的命令行入口：宿主以该开关启动一个应用实例，该实例用自身权限枚举物理驱动器，并写出宿主
/// 读取的报告。</para>
/// </summary>
/// <remarks>
/// The helper reports what it could not resolve instead of failing silently: a drive whose capacity or metadata
/// could not be read is listed with the reason, so the host can name the missing information rather than show an
/// empty list.
/// <para>
/// 助手会报告无法解析的内容而不是静默失败：容量或元数据读不到的磁盘连同原因一起列出，使宿主可以点名缺失的
/// 信息，而不是显示空列表。
/// </para>
/// </remarks>
public static class DiskScanChild
{
    /// <summary>
    /// The switch selecting the disk scan helper.
    /// <para>选择磁盘扫描助手的开关。</para>
    /// </summary>
    public const string Switch = "--disk-scan-job";

    /// <summary>
    /// The switch carrying the file the helper writes the report to.
    /// <para>携带助手写入报告文件的开关。</para>
    /// </summary>
    public const string OutputSwitch = "--disk-scan-output";

    /// <summary>
    /// The exit code of a helper that produced a complete report.
    /// <para>产生完整报告的助手的退出码。</para>
    /// </summary>
    public const int ExitSucceeded = 0;

    /// <summary>
    /// The exit code of a helper that could not enumerate the drives; the cause is in the report file.
    /// <para>无法枚举驱动器的助手的退出码；原因在报告文件中。</para>
    /// </summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// The exit code of a helper started with unusable arguments.
    /// <para>以不可用参数启动助手的退出码。</para>
    /// </summary>
    public const int ExitInvalidArguments = 2;

    /// <summary>
    /// Gets whether the arguments select the scan helper.
    /// <para>获取参数是否选择扫描助手。</para>
    /// </summary>
    /// <param name="args">The process arguments.<para>进程参数。</para></param>
    /// <returns><see langword="true"/> when the first argument is the helper switch.
    /// <para>首个参数为助手开关时为 <see langword="true"/>。</para></returns>
    public static bool IsRequested(string[] args) => args is [Switch, ..];

    /// <summary>
    /// Enumerates the drives and writes the report.
    /// <para>枚举驱动器并写出报告。</para>
    /// </summary>
    /// <param name="args">The process arguments.<para>进程参数。</para></param>
    /// <returns>The helper exit code.<para>助手退出码。</para></returns>
    public static int Run(string[] args)
    {
        var outputPath = FindValue(args, OutputSwitch);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return ExitInvalidArguments;
        }

        try
        {
            var drives = DriveScanner.CreateDefault()
                .ScanAsync()
                .GetAwaiter()
                .GetResult();
            var issues = new List<string>();
            foreach (var drive in drives)
            {
                if (drive.Size <= 0)
                {
                    issues.Add($"{drive.DevicePath} reported no capacity.");
                }
            }

            WriteReport(outputPath, new DiskScanReport { Drives = drives, Issues = issues });
            return ExitSucceeded;
        }
        catch (Exception ex)
        {
            WriteText(outputPath, FlashProgressFormat.WriteError(ex) + "\n");
            return ExitFailed;
        }
    }

    private static void WriteReport(string path, DiskScanReport report) =>
        WriteText(path, DiskScanFormat.Write(report));

    private static void WriteText(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
}