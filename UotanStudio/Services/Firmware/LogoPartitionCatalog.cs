using UotanStudio.Firmware.BootSecurity.LogoPartition;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// One partition found beside a logo partition, with the capability marker used by the logo tool.
/// <para>在 Logo 分区旁找到的一个分区，携带 Logo 工具所用的能力标记。</para>
/// </summary>
/// <param name="Name">Partition name derived from the file name. <para>由文件名推导出的分区名。</para></param>
/// <param name="Path">Absolute file path, present even when the file is missing. <para>绝对文件路径；文件不存在时仍保留路径。</para></param>
/// <param name="Exists">Whether the partition image exists on disk. <para>分区镜像是否在磁盘上存在。</para></param>
/// <param name="Length">On-disk length in bytes, or zero when missing. <para>磁盘字节长度；文件缺失时为 0。</para></param>
/// <param name="IsLogoContainer">Whether the file carries a logo or splash container magic.
/// <para>文件是否带 Logo 或 splash 容器魔数。</para></param>
public sealed record RelatedPartitionItem(
    string Name,
    string Path,
    bool Exists,
    long Length,
    bool IsLogoContainer);

/// <summary>
/// Names and sibling-file discovery for the boot/display partition group that accompanies a logo
/// partition. The list is a curated discovery aid, not a
/// partition table, so missing entries are reported instead of invented.
/// <para>与 Logo 分区同属启动/显示链的分区名清单与同目录发现逻辑。清单仅作发现辅助而非分区表，
/// 缺失项如实上报而不虚构。</para>
/// </summary>
internal static class LogoPartitionCatalog
{
    /// <summary>Partition names probed next to the opened logo image.
    /// <para>在打开的 Logo 镜像旁探测的分区名。</para></summary>
    /// <value>Ordered names in the boot/display chain. <para>启动/显示链中的有序名称。</para></value>
    public static IReadOnlyList<string> RelatedPartitionNames { get; } =
    [
        "logo", "splash", "bootlogo", "boot", "vendor_boot", "init_boot", "recovery",
        "dtbo", "dtb", "super", "vbmeta", "vbmeta_system", "misc", "frp", "persist", "lk", "lk2", "para"
    ];

    private static readonly string[] CandidateExtensions = [".img", ".bin"];

    /// <summary>
    /// Reports whether a file name looks like a logo or splash partition image; the check never reads
    /// the file system, so it is safe to evaluate while binding an explorer row.
    /// <para>判断文件名是否像 Logo 或 splash 分区镜像；该判断不访问文件系统，可安全地在资源管理器行绑定时求值。</para>
    /// </summary>
    /// <param name="path">File name or full path. <para>文件名或完整路径。</para></param>
    /// <returns>True for a recognized logo/splash file name. <para>已识别的 Logo/splash 文件名返回 true。</para></returns>
    public static bool IsLogoFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return CandidateExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && (name.Equals("logo", StringComparison.OrdinalIgnoreCase)
                || name.Equals("splash", StringComparison.OrdinalIgnoreCase)
                || name.Equals("bootlogo", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("logo_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("splash_", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Probes the sibling directory for the related partition group and reports which files exist,
    /// their sizes and whether their header carries a logo container magic.
    /// <para>探测同目录的相关分区组，上报文件是否存在、大小以及文件头是否带 Logo 容器魔数。</para>
    /// </summary>
    /// <param name="logoPath">Path of the opened logo partition. <para>已打开的 Logo 分区路径。</para></param>
    /// <returns>One row per known partition name. <para>每个已知分区名对应一行。</para></returns>
    public static IReadOnlyList<RelatedPartitionItem> Scan(string logoPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(logoPath));
        if (string.IsNullOrEmpty(directory))
        {
            return [];
        }

        var rows = new List<RelatedPartitionItem>(RelatedPartitionNames.Count);
        foreach (var name in RelatedPartitionNames)
        {
            var found = CandidateExtensions
                .Select(extension => Path.Combine(directory, name + extension))
                .FirstOrDefault(File.Exists);
            var path = found ?? Path.Combine(directory, name + CandidateExtensions[0]);
            long length = 0;
            var isLogo = false;
            if (found is not null)
            {
                try
                {
                    var info = new FileInfo(found);
                    length = info.Length;
                    isLogo = IsLogoContainer(found, info.Length);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    length = 0;
                }
            }

            rows.Add(new RelatedPartitionItem(name, path, found is not null, length, isLogo));
        }

        return rows;
    }

    private static bool IsLogoContainer(string path, long length)
    {
        if (length > LogoPartitionFormat.MaximumFileBytes)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var size = (int)Math.Min(length, 64 * 1024);
            var header = new byte[size];
            stream.ReadExactly(header);
            return LogoPartitionFormat.Detect(header) != LogoPartitionKind.Unknown;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}