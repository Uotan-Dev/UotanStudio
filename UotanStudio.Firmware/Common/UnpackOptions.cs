namespace UotanStudio.Firmware.Common;

public sealed class UnpackOptions
{
    public bool Overwrite { get; set; } = true;

    public IFirmwareProgress? Progress { get; set; }
}
