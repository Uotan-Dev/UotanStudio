namespace UotanStudio.DD.Drives.MacOS;

/// <summary>
/// Enumerates macOS disks through <c>diskutil</c> and exposes their raw device paths.
/// <para>通过 <c>diskutil</c> 枚举 macOS 磁盘并暴露其裸设备路径。</para>
/// </summary>
internal sealed class MacDriveScanner : IDriveScanner
{
    private const string DiskUtilPath = "/usr/sbin/diskutil";

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listXml = await MacCommand.RunAsync(DiskUtilPath, ["list", "-plist"], cancellationToken).ConfigureAwait(false);
        var listRoot = ParseOutput(listXml, "list");
        var disks = MacPlist.GetArray(listRoot, "AllDisksAndPartitions");
        var drives = new List<FlashDrive>(disks.Count);
        foreach (var diskNode in disks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identifier = MacPlist.GetString(diskNode, "DeviceIdentifier");
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            var infoXml = await MacCommand
                .RunAsync(DiskUtilPath, ["info", "-plist", identifier], cancellationToken)
                .ConfigureAwait(false);
            var info = ParseOutput(infoXml, $"info {identifier}");
            if (string.Equals(
                    MacPlist.GetString(info, "VirtualOrPhysical"),
                    "Virtual",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var size = MacPlist.GetInt64(diskNode, "Size") ?? MacPlist.GetInt64(info, "TotalSize") ?? 0;
            var removable = (MacPlist.GetBoolean(info, "RemovableMedia") ?? false)
                || (MacPlist.GetBoolean(info, "Ejectable") ?? false);
            var internalDisk = MacPlist.GetBoolean(info, "Internal") ?? false;
            var readOnly = (MacPlist.GetBoolean(info, "ReadOnlyMedia") ?? false)
                || (MacPlist.GetBoolean(info, "ReadOnlyVolume") ?? false);
            var mountPoints = CollectMountPoints(diskNode);
            var isSystem = mountPoints.Contains("/") || info is null || (internalDisk && !removable);
            var mediaName = MacPlist.GetString(info, "MediaName")
                ?? MacPlist.GetString(info, "VolumeName")
                ?? identifier;
            var bus = MacPlist.GetString(info, "BusProtocol") ?? string.Empty;
            var description = bus.Length > 0 ? $"{mediaName} ({bus})" : mediaName;

            drives.Add(new FlashDrive
            {
                DevicePath = ToRawDevicePath(identifier),
                DisplayName = mediaName,
                Size = size,
                IsRemovable = removable,
                IsReadOnly = readOnly,
                IsSystem = isSystem,
                Description = description,
                MountPoints = mountPoints,
            });
        }

        return drives;
    }

    private static object? ParseOutput(string xml, string command)
    {
        try
        {
            return MacPlist.Parse(xml);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new FlashException($"macOS diskutil {command} returned an unreadable property list.", exception);
        }
    }

    private static IReadOnlyList<string> CollectMountPoints(object? diskNode)
    {
        var points = new List<string>();
        AddPoint(MacPlist.GetString(diskNode, "MountPoint"));
        foreach (var partition in MacPlist.GetArray(diskNode, "Partitions"))
        {
            AddPoint(MacPlist.GetString(partition, "MountPoint"));
        }

        return points;

        void AddPoint(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !points.Contains(value, StringComparer.Ordinal))
            {
                points.Add(value);
            }
        }
    }

    private static string ToRawDevicePath(string identifier)
    {
        var bare = identifier.StartsWith("/dev/", StringComparison.Ordinal) ? identifier[5..] : identifier;
        bare = bare.TrimStart('/');
        return bare.StartsWith("rdisk", StringComparison.OrdinalIgnoreCase)
            ? "/dev/" + bare
            : "/dev/r" + bare;
    }
}