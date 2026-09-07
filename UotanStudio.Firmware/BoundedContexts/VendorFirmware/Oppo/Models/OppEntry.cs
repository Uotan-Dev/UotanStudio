namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

public sealed class OppEntry
{
    public string Name { get; init; } = string.Empty;

    internal long Offset { get; init; }

    public long Size { get; init; }

    internal long CompressedSize { get; init; }

    public long EncryptedSize { get; init; }

    public bool IsEncrypted => EncryptedSize > 0;

    public bool IsCompressed { get; init; }

    public string? PartitionType { get; init; }

    public string? Sha256 { get; init; }

    public string? Md5 { get; init; }

    internal long SectorOffset { get; init; }

    internal long SectorSize { get; init; }

    public string? FileName { get; init; }

    public bool IsSparse { get; init; }
}
