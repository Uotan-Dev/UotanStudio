using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.MacOS;

namespace UotanStudio.DD.Writing.MacOS;

/// <summary>
/// Records an ISO image to a macOS optical recorder with <c>drutil burn</c>.
/// <para>使用 <c>drutil burn</c> 把 ISO 镜像刻录到 macOS 光驱。</para>
/// </summary>
internal sealed class MacOpticalDiscWriter(FlashDrive drive) : OpticalDiscWriterBase(drive)
{
    /// <inheritdoc />
    protected override async Task ConfirmWritableMediaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = await MacOpticalDevice
            .ResolveIndexAsync(Drive.DevicePath, cancellationToken)
            .ConfigureAwait(false);
        if (index is null)
        {
            throw new FlashException(
                "The optical recorder is no longer present. Rescan the drives and try again.");
        }

        var status = await MacCommand.RunAsync(
            MacOpticalDevice.DrutilPath,
            MacOpticalDevice.StatusArguments(index.Value),
            cancellationToken).ConfigureAwait(false);
        if (!MacOpticalDevice.ParseStatus(status).Writable)
        {
            throw new FlashException(
                "The optical recorder no longer has writable media loaded. Insert a blank or rewritable disc.");
        }
    }

    /// <inheritdoc />
    protected override async Task WriteCoreAsync(
        OpticalDiscSession session,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var index = await MacOpticalDevice
            .ResolveIndexAsync(Drive.DevicePath, cancellationToken)
            .ConfigureAwait(false);
        if (index is null)
        {
            throw new FlashException(
                "The optical recorder is no longer present. Rescan the drives and try again.");
        }

        await OpticalCommand.RunAsync(
            MacOpticalDevice.DrutilPath,
            OpticalBurnArguments.DrutilBurn(index.Value, session.ImagePath, session.CloseSession),
            cancellationToken).ConfigureAwait(false);
    }
}