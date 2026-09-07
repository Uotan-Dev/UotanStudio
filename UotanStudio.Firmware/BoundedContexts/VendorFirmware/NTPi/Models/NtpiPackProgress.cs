namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

public class NtpiPackProgress
{
    public int TotalFiles { get; init; }

    public int CompletedFiles { get; init; }

    public string CurrentFileName { get; init; } = string.Empty;

    public NtpiPackPhase Phase { get; init; }
}

public enum NtpiPackPhase
{
    ProcessingFiles = 0,

    WritingRegions = 1,

    WritingRegion6 = 2,

    Completed = 3
}
