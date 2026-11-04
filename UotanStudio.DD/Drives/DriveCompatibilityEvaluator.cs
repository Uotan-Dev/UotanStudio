using UotanStudio.DD.Images;

namespace UotanStudio.DD.Drives;

/// <summary>
/// Applies the drive-safety rules to a scanned drive and a probed image.
/// <para>将驱动器安全规则应用到已扫描驱动与已探测镜像。</para>
/// </summary>
/// <remarks>
/// A locked or too-small drive is an error; a system drive, a large drive or a drive that
/// contains the source file produces warnings (the first matching branch wins).
/// <para>
/// 写保护或容量不足属于错误；系统盘、超大容量盘或包含源文件的盘产生警告（按首个命中的分支返回）。
/// </para>
/// </remarks>
public static class DriveCompatibilityEvaluator
{
    /// <summary>
    /// Drives larger than this threshold require explicit confirmation.
    /// <para>大于此阈值的驱动器需要显式确认。</para>
    /// </summary>
    public const long LargeDriveThresholdBytes = 128_000_000_000L;

    /// <summary>
    /// Evaluates one drive.
    /// <para>评估单个驱动器。</para>
    /// </summary>
    /// <param name="drive">Drive reported by the platform scanner.<para>平台扫描器上报的驱动器。</para></param>
    /// <param name="image">Probed image, or <see langword="null"/> when only selecting a drive.
    /// <para>已探测的镜像；仅选择驱动器时可为 <see langword="null"/>。</para></param>
    /// <param name="writing">Whether the operation will write to the drive.
    /// <para>本次操作是否会写入该驱动器。</para></param>
    /// <returns>
    /// Findings in evaluation order; empty when the drive is acceptable.
    /// <para>按评估顺序排列的发现；驱动可接受时为空。</para>
    /// </returns>
    public static IReadOnlyList<DriveCompatibilityIssueInfo> Evaluate(FlashDrive drive, FlashImage? image, bool writing)
    {
        ArgumentNullException.ThrowIfNull(drive);

        var issues = new List<DriveCompatibilityIssueInfo>(2);
        if (writing && drive.IsReadOnly)
        {
            issues.Add(new DriveCompatibilityIssueInfo(DriveCompatibilityIssue.Locked, DriveCompatibilitySeverity.Error));
        }

        if (image is not null && !IsDriveLargeEnough(drive, image))
        {
            issues.Add(new DriveCompatibilityIssueInfo(DriveCompatibilityIssue.TooSmall, DriveCompatibilitySeverity.Error));
            return issues;
        }

        if (drive.IsSystem)
        {
            issues.Add(new DriveCompatibilityIssueInfo(DriveCompatibilityIssue.SystemDrive, DriveCompatibilitySeverity.Warning));
        }
        else if (drive.Size > LargeDriveThresholdBytes)
        {
            issues.Add(new DriveCompatibilityIssueInfo(DriveCompatibilityIssue.LargeDrive, DriveCompatibilitySeverity.Warning));
        }

        if (image is not null && ContainsSourceImage(drive, image.Path))
        {
            issues.Add(new DriveCompatibilityIssueInfo(DriveCompatibilityIssue.ContainsImage, DriveCompatibilitySeverity.Error));
        }

        return issues;
    }

    /// <summary>
    /// Determines whether the drive can hold the image.
    /// <para>判断驱动器是否能容纳镜像。</para>
    /// </summary>
    /// <param name="drive">Candidate drive.<para>候选驱动器。</para></param>
    /// <param name="image">Probed image.<para>已探测的镜像。</para></param>
    /// <returns>
    /// <see langword="true"/> when the capacity is unknown or sufficient; when the image size is
    /// estimated only its on-disk (possibly compressed) size is compared.
    /// <para>
    /// 容量未知或足够时为 <see langword="true"/>；镜像大小仅为估算值时只比较磁盘上的
    /// （可能已压缩的）大小。
    /// </para>
    /// </returns>
    public static bool IsDriveLargeEnough(FlashDrive drive, FlashImage image)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(image);

        if (drive.Size <= 0)
        {
            return true;
        }

        var requiredBytes = image.Size ?? image.CompressedSize;
        return drive.Size >= requiredBytes;
    }

    /// <summary>
    /// Determines whether the image file currently resides on the drive.
    /// <para>判断镜像文件当前是否位于该驱动器上。</para>
    /// </summary>
    /// <param name="drive">Candidate drive.<para>候选驱动器。</para></param>
    /// <param name="imagePath">Path to the source image.<para>源镜像路径。</para></param>
    /// <returns><see langword="true"/> when the image path is under one of the drive's mount points.
    /// <para>当镜像路径位于任一挂载点之下时为 <see langword="true"/>。</para></returns>
    public static bool ContainsSourceImage(FlashDrive drive, string imagePath)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (drive.MountPoints.Count == 0)
        {
            return false;
        }

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var imageFullPath = Path.GetFullPath(imagePath);
        foreach (var mountPoint in drive.MountPoints)
        {
            if (string.IsNullOrWhiteSpace(mountPoint))
            {
                continue;
            }

            var mountFullPath = Path.GetFullPath(mountPoint);
            if (!mountFullPath.EndsWith(Path.DirectorySeparatorChar) && !mountFullPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                mountFullPath += Path.DirectorySeparatorChar;
            }

            if (imageFullPath.StartsWith(mountFullPath, comparison)
                || string.Equals(imageFullPath, mountFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison))
            {
                return true;
            }
        }

        return false;
    }
}