namespace UotanStudio.DD.Drives.Linux;

/// <summary>
/// Enumerates optical recorders from <c>/sys/class/block/sr*</c> on Linux.
/// <para>在 Linux 上从 <c>/sys/class/block/sr*</c> 枚举光驱。</para>
/// </summary>
/// <remarks>
/// Only the SCSI CD-ROM class devices that expose a <c>capabilities</c> directory with a writable
/// <c>device</c> link are reported, and the disc currently loaded (if any) is classified from
/// <c>/proc/sys/dev/cdrom/info</c> so callers can tell CD, DVD and Blu-ray media apart.
/// <para>
/// 只报告暴露 <c>capabilities</c> 目录且 <c>device</c> 链接可写的 SCSI CD-ROM 类设备；
/// 当前装入的光盘类型从 <c>/proc/sys/dev/cdrom/info</c> 分类，使调用方能区分 CD、DVD 与蓝光介质。
/// </para>
/// </remarks>
internal sealed class LinuxOpticalDriveScanner : IDriveScanner
{
    private const string SrClassRoot = "/sys/class/block";
    private const string CdromInfoPath = "/proc/sys/dev/cdrom/info";

    /// <inheritdoc />
    public Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(SrClassRoot))
        {
            return Task.FromResult<IReadOnlyList<FlashDrive>>([]);
        }

        var driveNames = Directory.EnumerateDirectories(SrClassRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith("sr", StringComparison.Ordinal)
                && name.Length > 2 && char.IsDigit(name[2]))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (driveNames.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<FlashDrive>>([]);
        }

        var driveCaps = ReadCdromInfo(driveNames);
        var drives = new List<FlashDrive>(driveNames.Length);
        foreach (var name in driveNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(SrClassRoot, name);
            var devicePath = "/dev/" + name;
            var vendor = ReadAttribute(Path.Combine(directory, "device", "vendor"));
            var model = ReadAttribute(Path.Combine(directory, "device", "model"));
            var description = string.Join(' ', new[] { vendor, model }.Where(part => part.Length > 0));
            if (description.Length == 0)
            {
                description = name;
            }

            var (format, writable) = driveCaps.TryGetValue(name, out var caps)
                ? (caps.Format, caps.Writable)
                : (OpticalDiscFormat.Unknown, false);

            drives.Add(new FlashDrive
            {
                DevicePath = devicePath,
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

        return Task.FromResult<IReadOnlyList<FlashDrive>>(drives);
    }

    internal static IReadOnlyDictionary<string, (OpticalDiscFormat Format, bool Writable)> ReadCdromInfo(
        IReadOnlyList<string> driveNames)
    {
        if (!File.Exists(CdromInfoPath))
        {
            return new Dictionary<string, (OpticalDiscFormat, bool)>(StringComparer.Ordinal);
        }

        string[]? names = null;
        string[]? cdRead = null;
        string[]? dvdRead = null;
        string[]? bdRead = null;
        string[]? canWrite = null;
        foreach (var line in File.ReadLines(CdromInfoPath))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var values = line[(separator + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (key)
            {
                case "drive name":
                    names = values;
                    break;
                case "CD-R":
                    cdRead = values;
                    break;
                case "DVD+R/DVD-R":
                case "DVD-R":
                    dvdRead = values;
                    break;
                case "BD-ROM":
                    bdRead = values;
                    break;
                case "can write":
                    canWrite = values;
                    break;
            }
        }

        var result = new Dictionary<string, (OpticalDiscFormat, bool)>(StringComparer.Ordinal);
        if (names is null)
        {
            return result;
        }

        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            if (!driveNames.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var format = HasFlag(bdRead, index) ? OpticalDiscFormat.BluRay
                : HasFlag(dvdRead, index) ? OpticalDiscFormat.Dvd
                : HasFlag(cdRead, index) ? OpticalDiscFormat.Cd
                : OpticalDiscFormat.Unknown;
            var writable = HasFlag(canWrite, index);
            result[name] = (format, writable);
        }

        return result;

        static bool HasFlag(string[]? flags, int index) =>
            flags is not null && index < flags.Length && flags[index] == "1";
    }

    private static string ReadAttribute(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}