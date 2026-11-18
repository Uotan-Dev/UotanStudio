namespace UotanStudio.DD.Drives.MacOS;

/// <summary>
/// Enumerates optical recorders on macOS through <c>drutil list</c> and <c>drutil status</c>.
/// <para>在 macOS 上通过 <c>drutil list</c> 与 <c>drutil status</c> 枚举光驱。</para>
/// </summary>
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
                .RunAsync(MacOpticalDevice.DrutilPath, ["list"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FlashException)
        {
            return [];
        }

        var drives = new List<FlashDrive>();
        var index = 0;
        foreach (var line in listing.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MacOpticalDevice.TryParse(line, out var name, out var description))
            {
                continue;
            }

            var (format, writable) = await ReadStatusAsync(index, cancellationToken).ConfigureAwait(false);
            drives.Add(new FlashDrive
            {
                DevicePath = "/dev/r" + name,
                DisplayName = description.Length > 0 ? description : name,
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
            index++;
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
                    ["status", "-device", deviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    cancellationToken)
                .ConfigureAwait(false);
            var format = status.Contains("BD-", StringComparison.OrdinalIgnoreCase) ? OpticalDiscFormat.BluRay
                : status.Contains("DVD", StringComparison.OrdinalIgnoreCase) ? OpticalDiscFormat.Dvd
                : status.Contains("CD-", StringComparison.OrdinalIgnoreCase) ? OpticalDiscFormat.Cd
                : OpticalDiscFormat.Unknown;
            var writable = status.Contains("writable", StringComparison.OrdinalIgnoreCase)
                || status.Contains("blank", StringComparison.OrdinalIgnoreCase)
                || status.Contains("recordable", StringComparison.OrdinalIgnoreCase);
            return (format, writable);
        }
        catch (FlashException)
        {
            return (OpticalDiscFormat.Unknown, false);
        }
    }
}