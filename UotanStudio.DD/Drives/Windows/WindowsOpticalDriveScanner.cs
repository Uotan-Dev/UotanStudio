namespace UotanStudio.DD.Drives.Windows;

/// <summary>
/// Enumerates optical recorders on Windows through the built-in IMAPI2 automation objects.
/// <para>通过 Windows 内置 IMAPI2 自动化对象枚举光驱。</para>
/// </summary>
internal sealed class WindowsOpticalDriveScanner : IDriveScanner
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string output;
        try
        {
            output = await WindowsOpticalCommand.RunAsync(
                WindowsOpticalCommand.BuildListScript(), cancellationToken).ConfigureAwait(false);
        }
        catch (FlashException)
        {
            return [];
        }

        var drives = new List<FlashDrive>();
        foreach (var line in output.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = line.TrimEnd('\r');
            if (!record.StartsWith("RECORDER", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = record.Split('\t');
            if (fields.Length < 3 || string.IsNullOrWhiteSpace(fields[1]))
            {
                continue;
            }

            var id = fields[1];
            var description = fields[2];
            var mediaType = fields.Length > 3 && int.TryParse(fields[3], out var type) ? type : -1;
            var mediaState = fields.Length > 4 && int.TryParse(fields[4], out var state) ? state : -1;
            var format = MediaTypeToFormat(mediaType);
            var writable = IsWritableMedia(mediaType) && IsWritableState(mediaState);
            drives.Add(new FlashDrive
            {
                DevicePath = id,
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

    private static OpticalDiscFormat MediaTypeToFormat(int mediaType) => mediaType switch
    {
        1 or 2 or 3 => OpticalDiscFormat.Cd,
        >= 4 and <= 13 => OpticalDiscFormat.Dvd,
        0x11 or 0x12 or 0x13 => OpticalDiscFormat.BluRay,
        _ => OpticalDiscFormat.Unknown,
    };

    private static bool IsWritableMedia(int mediaType) => mediaType switch
    {
        2 or 3 => true,
        >= 5 and <= 13 => true,
        0x12 or 0x13 => true,
        _ => false,
    };

    private static bool IsWritableState(int mediaState)
    {
        const int errorMask = 0xFC00;
        const int writableBits = 0x1 | 0x2 | 0x4;
        return (mediaState & errorMask) == 0 && (mediaState & writableBits) != 0;
    }
}