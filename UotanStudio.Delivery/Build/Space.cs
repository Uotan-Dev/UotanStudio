namespace UotanStudio.Delivery;

/// <summary>Disk space estimation ahead of a delivery export. <para>交付输出前的磁盘剩余空间估算。</para></summary>
public static class DiskSpaceEstimator
{
    /// <summary><para>输出过程会同时产生中间文件与最终镜像，统一按源镜像总量的 2 倍估算需求。</para>
    /// The export writes intermediate files alongside the final images, so demand is estimated as twice the source image total.</summary>
    internal const long SafetyFactor = 2;

    /// <summary><para>每个启用平台在 stage/bin 下解压 platform-tools 的预留空间。</para>
    /// Reserve per enabled platform for platform-tools extracted under stage/bin.</summary>
    internal const long ToolAllowancePerPlatform = 64L * 1024 * 1024;

    /// <summary><para>按交付计划估算输出所需字节数：源镜像（Files + Container，去重）加上生成的
    /// 完整 Super（<see cref="DeliverySuperOptions.DeviceSize"/>，仅生成Super_Empty 时不计）、Payload
    /// 分区与工具预留，再乘安全系数。
    /// 缺失、过大或不可读的源文件按 0 计；只做有界加法，不抛异常。</para>
    /// Estimates the bytes a delivery export needs from the plan: source images (Files + Container, deduplicated) plus the
    /// generated full Super (<see cref="DeliverySuperOptions.DeviceSize"/>, excluded for Super_Empty only), Payload partitions and a tool
    /// allowance, all multiplied by the safety factor. Missing, oversized, or unreadable sources count as zero; addition is
    /// bounded and never throws.</summary>
    public static long EstimateExportDemand(DeliveryPlan plan)
    {
        if (plan.IsSuperEmptyOnly) return (4096L + plan.Super.MetadataSize) * SafetyFactor;
        var total = Add(SumSourceLengths(plan.Files), SumSourceLengths(plan.Container));
        if (plan.PackSuper && !plan.Super.Empty) total = Add(total, checked((long)plan.Super.DeviceSize));
        if (plan.OrganizeDirectory)
        {
            var platforms = (plan.Windows ? 1 : 0) + (plan.Linux ? 1 : 0) + (plan.MacOS ? 1 : 0);
            total = Add(total, ToolAllowancePerPlatform * platforms);
        }
        return total * SafetyFactor;
    }

    private static long SumSourceLengths(IEnumerable<DeliveryFile> files)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        long total = 0;
        foreach (var path in files.Where(f => f.SourcePath.Length > 0).Select(f => f.SourcePath).Distinct(comparison))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0) total = Add(total, info.Length);
            }
            catch { }
        }
        return total;
    }

    private static long Add(long total, long value) => value > 0 && total <= long.MaxValue - value ? total + value : total;

    /// <summary><para>估算输出所需字节数：对每个镜像文件取 <see cref="FileInfo.Length"/> 求和后乘以安全系数 2；
    /// 文件不存在、过大（超过 long 上限）或读取失败时按 0 计。</para>
    /// Estimates the bytes needed for an export: sums <see cref="FileInfo.Length"/> over every image and applies a
    /// safety factor of 2; missing, oversized, or unreadable files count as zero.</summary>
    /// <param name="outputDirectory"><para>输出目录路径（当前仅用于上下文表达，保留以便扩展）。</para>
    /// Output directory path (kept for context and future extension).</param>
    /// <param name="imagePaths">Full paths of the selected image files. <para>所选镜像文件的完整路径列表。</para></param>
    internal static long EstimateOutputBytes(string outputDirectory, IReadOnlyList<string> imagePaths)
    {
        long total = 0;
        foreach (var path in imagePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0 && total <= long.MaxValue - info.Length) total += info.Length;
            }
            catch { }
        }
        return total * SafetyFactor;
    }

    /// <summary><para>获取目录所在分区的剩余字节数；无法确定（含异常）时返回 -1。</para>
    /// Returns the free bytes on the drive hosting the directory, or -1 when it cannot be determined.</summary>
    /// <param name="directory"><para>目录路径；按其路径根（盘符/挂载点）查询。</para>
    /// Directory path; queried by its path root (drive letter / mount point).</param>
    public static long GetFreeBytes(string directory)
    {
        try
        {
            var root = GetVolumePath(directory);
            if (string.IsNullOrEmpty(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    /// <summary><para>查找最具体的挂载卷根路径，包含符号链接目录之下的目标。</para>
    /// Finds the most specific mounted volume root, including destinations below linked directories.</summary>
    public static string? GetVolumePath(string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(directory);
            for (var ancestor = new DirectoryInfo(fullPath); ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor.LinkTarget is null) continue;
                if (ancestor.ResolveLinkTarget(true) is { } resolved)
                    fullPath = Path.GetFullPath(Path.Combine(resolved.FullName, Path.GetRelativePath(ancestor.FullName, fullPath)));
                break;
            }
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return DriveInfo.GetDrives().Select(d => d.Name)
                .Where(root => fullPath.Equals(Path.TrimEndingDirectorySeparator(root), comparison)
                    || fullPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
                .OrderByDescending(root => root.Length).FirstOrDefault() ?? Path.GetPathRoot(fullPath);
        }
        catch { return null; }
    }
}