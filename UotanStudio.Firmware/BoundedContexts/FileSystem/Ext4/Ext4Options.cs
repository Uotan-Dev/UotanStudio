namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

public sealed class Ext4Options
{
    public int BlockSize { get; init; } = 4096;

    public int InodeSize { get; init; } = 256;

    public bool Ext4Compress { get; set; } = false;

    public bool EnableSparse { get; set; } = false;

    public byte[]? Uuid { get; init; }

    public bool PreservePermissions { get; set; } = true;

    public bool PreserveTimestamps { get; set; } = true;

    public bool EnableMetadataCsum { get; set; } = true;

    public bool IncludeJournal { get; set; } = true;
}
