namespace UotanStudio.DD.Drives.Linux;

/// <summary>
/// Reads <c>/sys/block</c> and <c>/proc/mounts</c> to describe Linux block devices.
/// <para>读取 <c>/sys/block</c> 与 <c>/proc/mounts</c> 以描述 Linux 块设备。</para>
/// </summary>
internal sealed class LinuxDriveScanner : IDriveScanner
{
    private const string SysBlockRoot = "/sys/block";
    private const string MountTablePath = "/proc/mounts";
    private const string MountInfoPath = "/proc/self/mountinfo";

    private static readonly string[] VirtualPrefixes =
    [
        "loop",
        "ram",
        "zram",
        "dm-",
        "md",
        "sr",
        "nbd",
        "fd",
        "rbd",
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(SysBlockRoot))
        {
            return Task.FromResult<IReadOnlyList<FlashDrive>>([]);
        }

        var disks = Directory.EnumerateDirectories(SysBlockRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null && IsPhysical(name))
            .Cast<string>()
            .ToArray();
        var mounts = ReadMounts();
        var parentByDevice = mounts
            .Select(mount => (mount, parent: FindParentDisk(mount.DeviceName, disks)))
            .Where(pair => pair.parent is not null)
            .ToArray();

        var rootDisk = TryGetRootDisk(mounts, disks);
        var systemUnknown = rootDisk is null;
        var drives = new List<FlashDrive>(disks.Length);
        foreach (var disk in disks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(SysBlockRoot, disk);
            var size = ReadLong(Path.Combine(directory, "size")) * 512;
            var removable = ReadAttribute(Path.Combine(directory, "removable")) == "1";
            var readOnly = ReadAttribute(Path.Combine(directory, "ro")) == "1";
            var description = BuildDescription(directory, disk);
            var mountPoints = parentByDevice
                .Where(pair => pair.parent == disk)
                .Select(pair => pair.mount.MountPoint)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var isSystem = systemUnknown ? !removable : disk == rootDisk;

            drives.Add(new FlashDrive
            {
                DevicePath = "/dev/" + disk,
                DisplayName = description,
                Size = size,
                IsRemovable = removable,
                IsReadOnly = readOnly,
                IsSystem = isSystem,
                Description = description,
                MountPoints = mountPoints,
            });
        }

        return Task.FromResult<IReadOnlyList<FlashDrive>>(drives);
    }

    private static bool IsPhysical(string name) =>
        VirtualPrefixes.All(prefix => !name.StartsWith(prefix, StringComparison.Ordinal))
        && !(name.StartsWith("mmcblk", StringComparison.Ordinal)
            && (name.Contains("boot", StringComparison.Ordinal) || name.Contains("rpmb", StringComparison.Ordinal)));

    private static string BuildDescription(string diskDirectory, string fallback)
    {
        var vendor = ReadAttribute(Path.Combine(diskDirectory, "device", "vendor"));
        var model = ReadAttribute(Path.Combine(diskDirectory, "device", "model"));
        if (vendor.Length == 0 && model.Length == 0)
        {
            vendor = ReadAttribute(Path.Combine(diskDirectory, "device", "name"));
        }

        var description = string.Join(' ', new[] { vendor, model }.Where(part => part.Length > 0));
        return description.Length > 0 ? description : fallback;
    }

    private static string? FindParentDisk(string deviceName, IReadOnlyList<string> disks)
    {
        if (disks.Contains(deviceName))
        {
            return deviceName;
        }

        foreach (var disk in disks)
        {
            if (Directory.Exists(Path.Combine(SysBlockRoot, disk, deviceName)))
            {
                return disk;
            }
        }

        return null;
    }

    private static IReadOnlyList<MountEntry> ReadMounts()
    {
        if (!File.Exists(MountTablePath))
        {
            return [];
        }

        var entries = new List<MountEntry>();
        foreach (var line in File.ReadLines(MountTablePath))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !fields[0].StartsWith("/dev/", StringComparison.Ordinal))
            {
                continue;
            }

            entries.Add(new MountEntry(Path.GetFileName(fields[0]), UnescapeMountPoint(fields[1])));
        }

        return entries;
    }

    private static string? TryGetRootDisk(IReadOnlyList<MountEntry> mounts, IReadOnlyList<string> disks)
    {
        foreach (var mount in mounts)
        {
            if (mount.MountPoint == "/" && FindParentDisk(mount.DeviceName, disks) is { } parent)
            {
                return parent;
            }
        }

        if (!File.Exists(MountInfoPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(MountInfoPath))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || UnescapeMountPoint(fields[4]) != "/")
            {
                continue;
            }

            var deviceNumber = fields[2];
            var linkTarget = new DirectoryInfo(Path.Combine("/sys/dev/block", deviceNumber)).LinkTarget;
            if (linkTarget is null)
            {
                continue;
            }

            var segments = linkTarget.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var blockIndex = Array.LastIndexOf(segments, "block");
            if (blockIndex >= 0 && blockIndex + 1 < segments.Length)
            {
                var candidate = segments[blockIndex + 1];
                if (disks.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string UnescapeMountPoint(string value)
    {
        return value
            .Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
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

    private static long ReadLong(string path)
    {
        var text = ReadAttribute(path);
        return long.TryParse(text, out var value) ? value : 0;
    }

    private sealed record MountEntry(string DeviceName, string MountPoint);
}