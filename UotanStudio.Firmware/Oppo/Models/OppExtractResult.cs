namespace UotanStudio.Firmware.Oppo.Models;

public sealed class OppExtractResult
{
    public int ExtractedFileCount { get; init; }

    public string OutputDirectory { get; init; } = string.Empty;

    public string? KeyId { get; init; }
}
