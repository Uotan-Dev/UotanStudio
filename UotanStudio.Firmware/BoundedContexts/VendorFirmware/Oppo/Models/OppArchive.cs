namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

public sealed class OppArchive
{
    public OppMetadata Metadata { get; init; } = new();

    public IReadOnlyList<OppEntry> Entries { get; init; } = [];
}