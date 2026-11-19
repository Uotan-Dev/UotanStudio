namespace UotanStudio.DD.Drives.MacOS;

/// <summary>
/// Enumerates optical recorders on macOS through <c>drutil list -xml</c> and <c>drutil -drive N status -xml</c>.
/// <para>在 macOS 上通过 <c>drutil list -xml</c> 与 <c>drutil -drive N status -xml</c> 枚举光驱。</para>
/// </summary>
/// <remarks>
/// A recorder is always reported, even without media, so the caller can distinguish "no recorder attached"
/// from "recorder attached but no disc loaded". <see cref="FlashDrive.IsDiscWritable"/> carries the media state.
/// <para>
/// 即便没有介质也始终上报刻录机，便于调用方区分“没有接刻录机”与“接着刻录机但没放光盘”；
/// 介质状态由 <see cref="FlashDrive.IsDiscWritable"/> 承载。
/// </para>
/// </remarks>
internal sealed class MacOpticalDriveScanner : IDriveScanner
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string listing;
        try
        {
            listing = await MacCommand
                .RunAsync(MacOpticalDevice.DrutilPath, ["list", "-xml"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FlashException)
        {
            return [];
        }

        var drives = new List<FlashDrive>();
        foreach (var (index, name, description) in MacOpticalDevice.ParseList(listing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (format, writable) = await ReadStatusAsync(index, cancellationToken).ConfigureAwait(false);
            drives.Add(new FlashDrive
            {
                DevicePath = name,
                DisplayName = description,
                Size = 0,
                IsRemovable = true,
                IsReadOnly = false,
                IsSystem = false,
                Description = description,
                MountPoints = [],
                MediaKind = FlashMediaKind.OpticalDisc,
                DiscFormat = format,
                IsDiscWritable = writable,
            });
        }

        return drives;
    }

    private static async Task<(OpticalDiscFormat Format, bool Writable)> ReadStatusAsync(
        int deviceIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await MacCommand
                .RunAsync(
                    MacOpticalDevice.DrutilPath,
                    MacOpticalDevice.StatusArguments(deviceIndex),
                    cancellationToken)
                .ConfigureAwait(false);
            return MacOpticalDevice.ParseStatus(status);
        }
        catch (FlashException)
        {
            return (OpticalDiscFormat.Unknown, false);
        }
    }
}