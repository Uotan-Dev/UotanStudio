using System.Runtime.InteropServices;
using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing.Unix;

/// <summary>
/// Probes block-device write access on Linux and macOS by opening the device node and checking the effective uid.
/// <para>在 Linux 与 macOS 上通过打开设备节点并检查有效用户 ID 来探测块设备写权限。</para>
/// </summary>
internal sealed class UnixDiskWriteProbe : IDiskWriteProbe
{
    /// <inheritdoc />
    public bool IsElevated => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
        ? geteuid() == 0
        : false;

    /// <inheritdoc />
    public Task<bool?> CanWriteAsync(FlashDrive drive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var stream = new FileStream(
                drive.DevicePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.None);
            return Task.FromResult<bool?>(true);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<bool?>(false);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult<bool?>(null);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}