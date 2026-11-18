using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing.Linux;

/// <summary>
/// Records an ISO image to a Linux optical device with <c>xorriso</c>.
/// <para>使用 <c>xorriso</c> 把 ISO 镜像刻录到 Linux 光驱。</para>
/// </summary>
internal sealed class LinuxOpticalDiscWriter(FlashDrive drive) : OpticalDiscWriterBase(drive)
{
    /// <inheritdoc />
    protected override Task WriteCoreAsync(OpticalDiscSession session, CancellationToken cancellationToken) =>
        OpticalCommand.RunAsync(
            "xorriso",
            OpticalBurnArguments.Xorriso(Drive.DevicePath, session.ImagePath, session.CloseSession),
            cancellationToken);
}