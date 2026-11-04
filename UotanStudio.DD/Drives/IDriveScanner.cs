namespace UotanStudio.DD.Drives;

/// <summary>
/// Enumerates physical disks that may receive a disk image.
/// <para>枚举可接收磁盘镜像的物理磁盘。</para>
/// </summary>
public interface IDriveScanner
{
    /// <summary>
    /// Scans the machine and returns one entry per physical drive.
    /// <para>扫描本机并返回每个物理驱动器一项。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the scan.<para>取消扫描。</para></param>
    /// <returns>Drives in platform enumeration order, with duplicates merged.
    /// <para>按平台枚举顺序排列、已合并重复项的驱动器列表。</para></returns>
    /// <exception cref="FlashException">A platform command failed and no drives could be read.
    /// <para>平台命令失败且无法读取任何驱动器。</para></exception>
    Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the scanner for the current operating system.
/// <para>为当前操作系统创建扫描器。</para>
/// </summary>
public static class DriveScanner
{
    /// <summary>
    /// Creates a scanner that uses Windows device IOCTLs, Linux sysfs or macOS <c>diskutil</c>.
    /// <para>创建使用 Windows 设备 IOCTL、Linux sysfs 或 macOS <c>diskutil</c> 的扫描器。</para>
    /// </summary>
    /// <returns>The platform scanner.<para>平台扫描器。</para></returns>
    public static IDriveScanner CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new Windows.WindowsDriveScanner();
        }

        if (OperatingSystem.IsLinux())
        {
            return new Linux.LinuxDriveScanner();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOS.MacDriveScanner();
        }

        throw new PlatformNotSupportedException("Drive scanning is supported on Windows, Linux and macOS only.");
    }
}