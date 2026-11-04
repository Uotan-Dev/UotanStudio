using System.Text;

namespace UotanStudio.DD.Drives.Windows;

/// <summary>
/// Enumerates Windows volumes, maps them to physical disks and fills in drive metadata.
/// <para>枚举 Windows 卷，将其映射到物理磁盘并填充驱动器元数据。</para>
/// </summary>
internal sealed class WindowsDriveScanner : IDriveScanner
{
    private static readonly string SystemRoot = NormalizeRoot(
        (Environment.GetEnvironmentVariable("SystemDrive") ?? @"C:").TrimEnd('\\') + "\\");

    /// <inheritdoc />
    public Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var builders = new Dictionary<string, DriveBuilder>(StringComparer.OrdinalIgnoreCase);
        var volumeName = new StringBuilder(1024);
        var findHandle = WindowsDeviceApi.FindFirstVolume(volumeName, (uint)volumeName.Capacity);
        if (findHandle.ToInt64() == -1)
        {
            throw new FlashException("Windows volume enumeration could not start.", WindowsDeviceApi.LastError());
        }

        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var volume = volumeName.ToString();
                if (volume.Length > 0)
                {
                    AddVolume(builders, volume);
                }
            }
            while (WindowsDeviceApi.FindNextVolume(findHandle, volumeName, (uint)volumeName.Capacity));
        }
        finally
        {
            WindowsDeviceApi.CloseVolumeEnumeration(findHandle);
        }

        var drives = new List<FlashDrive>(builders.Count);
        foreach (var builder in builders.Values)
        {
            if (builder.Size <= 0)
            {
                builder.Size = builder.MountPoints.Sum(WindowsDeviceApi.GetVolumeCapacity);
            }

            drives.Add(new FlashDrive
            {
                DevicePath = builder.DevicePath,
                DisplayName = builder.Description.Length > 0 ? builder.Description : $"Disk {builder.DiskNumber}",
                Size = builder.Size,
                IsRemovable = builder.IsRemovable,
                IsReadOnly = builder.IsReadOnly,
                IsSystem = builder.IsSystem,
                Description = builder.Description,
                MountPoints = builder.MountPoints.ToArray(),
            });
        }

        return Task.FromResult<IReadOnlyList<FlashDrive>>(drives);
    }

    private static void AddVolume(Dictionary<string, DriveBuilder> builders, string volumeName)
    {
        var diskNumber = WindowsDeviceApi.TryGetDiskNumber(volumeName);
        if (diskNumber is null)
        {
            return;
        }

        var devicePath = $@"\\.\PhysicalDrive{diskNumber.Value}";
        if (!builders.TryGetValue(devicePath, out var builder))
        {
            builder = new DriveBuilder(devicePath, diskNumber.Value);
            builders.Add(devicePath, builder);
        }

        foreach (var mountPoint in WindowsDeviceApi.GetVolumeMountPoints(volumeName))
        {
            if (!builder.MountPoints.Exists(existing => string.Equals(existing, mountPoint, StringComparison.OrdinalIgnoreCase)))
            {
                builder.MountPoints.Add(mountPoint);
            }

            if (string.Equals(NormalizeRoot(mountPoint), SystemRoot, StringComparison.OrdinalIgnoreCase))
            {
                builder.IsSystem = true;
            }

            if (WindowsDeviceApi.GetDriveType(mountPoint) == WindowsDeviceApi.DriveRemovable)
            {
                builder.IsRemovable = true;
            }
        }

        if (builder.Size <= 0)
        {
            builder.Size = WindowsDeviceApi.TryGetDiskLength(devicePath) ?? 0;
        }

        if (!builder.DescriptorQueried)
        {
            builder.DescriptorQueried = true;
            if (WindowsDeviceApi.TryGetStorageDescriptor(devicePath) is { } descriptor)
            {
                builder.IsRemovable |= descriptor.Removable;
                builder.Description = descriptor.Description;
            }
        }

        if (!builder.WritableQueried)
        {
            builder.WritableQueried = true;
            if (WindowsDeviceApi.TryGetWritable(devicePath) is { } writable)
            {
                builder.IsReadOnly = !writable;
            }
        }
    }

    private static string NormalizeRoot(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private sealed class DriveBuilder
    {
        public DriveBuilder(string devicePath, int diskNumber)
        {
            DevicePath = devicePath;
            DiskNumber = diskNumber;
        }

        public string DevicePath { get; }

        public int DiskNumber { get; }

        public long Size { get; set; }

        public bool IsRemovable { get; set; }

        public bool IsReadOnly { get; set; }

        public bool IsSystem { get; set; }

        public string Description { get; set; } = string.Empty;

        public bool DescriptorQueried { get; set; }

        public bool WritableQueried { get; set; }

        public List<string> MountPoints { get; } = [];
    }
}