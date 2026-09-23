namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

public sealed class OppExtractOptions
{
    public bool Overwrite { get; init; }

    public string? KeyId { get; init; }

    /// <summary>Exact archive entry names to extract; null extracts every entry.</summary>
    public IReadOnlyCollection<string>? SelectedEntries { get; init; }
}