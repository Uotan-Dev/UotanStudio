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
                DiscFormat = OpticalDiscFormat.Unknown,
                IsDiscWritable = false,
            });
        }

        return drives;
    }
}