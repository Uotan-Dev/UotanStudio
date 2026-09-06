namespace UotanStudio.Firmware.NTPi.Models;

public class NtpiFileInfo
{
    public ulong MajorVersion { get; init; }

    public ulong MinorVersion { get; init; }

    public ulong PatchVersion { get; init; }

    public List<(RegionType Type, long Size)> Regions { get; init; } = [];

    public List<FileEntry> FileEntries { get; set; } = [];
}

public class NtpiParsedFile
{
    public NtpiHeader Header { get; init; }

    public Dictionary<RegionType, byte[]> Regions { get; init; } = [];

    public List<FileEntry> FileEntries { get; set; } = [];

    public Dictionary<string, byte[]> ExtractedFiles { get; init; } = [];
}

public class NtpiExtractionProgress
{
    public int TotalFiles { get; init; }

    public int CompletedFiles { get; init; }

    public string CurrentFileName { get; init; } = string.Empty;
}
