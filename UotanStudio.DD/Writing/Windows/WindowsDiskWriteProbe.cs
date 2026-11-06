using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.Windows;

namespace UotanStudio.DD.Writing.Windows;

/// <summary>
/// Probes raw-disk write access on Windows by opening the physical drive and checking the administrator token.
/// <para>在 Windows 上通过打开物理磁盘并检查管理员令牌来探测裸磁盘写权限。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDiskWriteProbe : IDiskWriteProbe
{
    /// <inheritdoc />
    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is SecurityException or ArgumentException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public Task<bool?> CanWriteAsync(FlashDrive drive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        cancellationToken.ThrowIfCancellationRequested();

        var handle = WindowsDeviceApi.TryOpen(
            drive.DevicePath,
            WindowsDeviceApi.GenericRead | WindowsDeviceApi.GenericWrite,
            out var error);
        if (handle is not null)
        {
            handle.Dispose();
            return Task.FromResult<bool?>(true);
        }

        return Task.FromResult<bool?>(FlashAccessClassifier.IsAccessDenied(error) ? false : null);
    }
}