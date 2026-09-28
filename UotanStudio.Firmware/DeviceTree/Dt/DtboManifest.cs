using System.Text.Json.Serialization;
using UotanStudio.Firmware.DeviceTree.Dt;

namespace UotanStudio.Firmware.DeviceTree.Dt;

internal sealed class DtboManifest
{
    public int FormatVersion { get; set; } = 1;
    public bool HasOriginalImage { get; set; }
    public long ImageSize { get; set; }
    public DtboHeader Header { get; set; } = new()
    {
        Magic = FdtConstants.DtboMagic,
        HeaderSize = FdtConstants.DtboHeaderSize,
        EntrySize = FdtConstants.DtboEntrySize,
        EntriesOffset = FdtConstants.DtboHeaderSize,
        PageSize = FdtConstants.DtboDefaultPageSize
    };
    public List<DtboManifestEntry> Entries { get; set; } = [];
}

internal sealed class DtboManifestEntry
{
    public string File { get; set; } = string.Empty;
    public DtboEntry Metadata { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, IgnoreReadOnlyProperties = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DtboManifest))]
internal partial class DtboJsonContext : JsonSerializerContext;