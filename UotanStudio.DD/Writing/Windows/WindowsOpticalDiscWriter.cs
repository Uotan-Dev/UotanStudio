using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.Windows;

namespace UotanStudio.DD.Writing.Windows;

/// <summary>
/// Records an ISO image to a Windows optical recorder through the built-in IMAPI2 automation objects.
/// <para>通过 Windows 内置 IMAPI2 自动化对象把 ISO 镜像刻录到光驱。</para>
/// </summary>
internal sealed class WindowsOpticalDiscWriter(FlashDrive drive) : OpticalDiscWriterBase(drive)
{
    /// <inheritdoc />
    protected override Task WriteCoreAsync(OpticalDiscSession session, CancellationToken cancellationToken) =>
        WindowsOpticalCommand.RunAsync(
            WindowsOpticalCommand.BuildBurnScript(
                Drive.DevicePath,
                session.ImagePath,
                session.CloseSession),
            cancellationToken);
}