namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip;

public sealed class RockchipPackOptions
{
    public string? Chip { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public uint? Code { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public bool ReuseHeaderTemplate { get; init; } = true;
}
