namespace UotanStudio.Firmware.Cpio.Model;

public sealed class ArchiveEntryMetadata
{
    public ulong Inode { get; init; }

    public uint Mode { get; init; }

    public uint UserId { get; init; }

    public uint GroupId { get; init; }

    public uint DeviceMajor { get; init; }

    public uint DeviceMinor { get; init; }

    public uint RDeviceMajor { get; init; }

    public uint RDeviceMinor { get; init; }

    public ulong LinkCount { get; init; }

    public long ModificationTimeUnixSeconds { get; init; }

    public uint Checksum { get; init; }

    public CpioFileType FileType { get; init; }

    public int UnixPermissions { get; init; }

    public IReadOnlyDictionary<string, byte[]> ExtendedAttributes { get; init; } = new Dictionary<string, byte[]>();
}
