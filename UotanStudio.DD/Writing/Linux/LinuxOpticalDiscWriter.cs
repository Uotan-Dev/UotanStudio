using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.Linux;

namespace UotanStudio.DD.Writing.Linux;

/// <summary>
/// Records an ISO image to a Linux optical device with <c>xorriso</c>.
/// <para>使用 <c>xorriso</c> 把 ISO 镜像刻录到 Linux 光驱。</para>
/// </summary>
internal sealed class LinuxOpticalDiscWriter(FlashDrive drive) : OpticalDiscWriterBase(drive)
{
    /// <inheritdoc />
    protected override async Task ConfirmWritableMediaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, writable) = await LinuxOpticalDriveScanner
            .ConfirmMediaStateAsync(Drive.DevicePath, cancellationToken)
            .ConfigureAwait(false);
        if (!writable)
        {
            throw new FlashException(
                "The optical recorder no longer has writable media loaded. Insert a blank or rewritable disc.");
        }
    }

    /// <inheritdoc />
    protected override Task WriteCoreAsync(
        OpticalDiscSession session,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken) =>
        OpticalCommand.RunAsync(
            "xorriso",
            OpticalBurnArguments.Xorriso(Drive.DevicePath, session.ImagePath, session.CloseSession),
            cancellationToken);
}