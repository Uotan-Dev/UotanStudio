namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

public sealed class OppMetadata
{
    public string FormatName { get; init; } = string.Empty;

    public OppFormat Format { get; init; }

    public Version? FormatVersion { get; init; }

    public long FileSize { get; init; }

    public int EntryCount { get; init; }

    public string? ProjectName { get; init; }

    public string? CpuModel { get; init; }

    public string? FlashType { get; init; }

    public string? FirmwareName { get; init; }

    public string? ProjectId { get; init; }

    public bool IsZipWrapped { get; init; }

    public string? KeyId { get; init; }
}
