using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Reports whether this process may write a scanned drive, so callers can ask for elevation before writing.
/// <para>报告本进程能否写入已扫描驱动器，使调用方可以在写入前请求提权。</para>
/// </summary>
public interface IDiskWriteProbe
{
    /// <summary>
    /// Gets whether the current process already holds the privilege raw disk writing requires.
    /// <para>获取当前进程是否已持有写入裸磁盘所需的权限。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> for an elevated Windows process and for a POSIX process running as root.
    /// <para>Windows 提权进程与以 root 运行的 POSIX 进程为 <see langword="true"/>。</para>
    /// </value>
    bool IsElevated { get; }

    /// <summary>
    /// Opens the raw device for writing and releases it again without writing anything.
    /// <para>以写方式打开裸设备后立即释放，不写入任何内容。</para>
    /// </summary>
    /// <param name="drive">Drive to probe.<para>待探测的驱动器。</para></param>
    /// <param name="cancellationToken">Cancels the probe.<para>取消探测。</para></param>
    /// <returns>
    /// <see langword="true"/> when the device opened for writing, <see langword="false"/> when the platform
    /// answered "access denied", and <see langword="null"/> when the probe could not decide.
    /// <para>设备以写方式打开成功时为 <see langword="true"/>；平台回答「拒绝访问」时为
    /// <see langword="false"/>；无法判定时为 <see langword="null"/>。</para>
    /// </returns>
    Task<bool?> CanWriteAsync(FlashDrive drive, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the write probe for the current operating system.
/// <para>为当前操作系统创建写权限探测器。</para>
/// </summary>
public static class DiskWriteProbe
{
    /// <summary>
    /// Creates the platform probe.
    /// <para>创建平台探测器。</para>
    /// </summary>
    /// <returns>The probe matching the running platform.<para>与当前平台匹配的探测器。</para></returns>
    /// <exception cref="PlatformNotSupportedException">The platform has no raw disk writer.
    /// <para>该平台没有裸磁盘写入器。</para></exception>
    public static IDiskWriteProbe CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new Windows.WindowsDiskWriteProbe();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new Unix.UnixDiskWriteProbe();
        }

        throw new PlatformNotSupportedException("Disk writing is supported on Windows, Linux and macOS only.");
    }
}