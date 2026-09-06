namespace UotanStudio.Firmware.Nb0;

public sealed class Nb0CheckProgress
{
    public int TotalEntries { get; set; }

    public int CompletedEntries { get; set; }

    public string CurrentEntryName { get; set; } = string.Empty;
}

public sealed class Nb0ExtractionProgress
{
    public int TotalEntries { get; set; }

    public int CompletedEntries { get; set; }

    public string CurrentEntryName { get; set; } = string.Empty;
}

public sealed class Nb0PackProgress
{
    public int TotalEntries { get; set; }

    public int CompletedEntries { get; set; }

    public string CurrentEntryName { get; set; } = string.Empty;
}
