namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;

public class VendorRamdiskTableEntry
{
    public uint RamdiskSize { get; set; }

    public uint RamdiskOffset { get; set; }

    public uint RamdiskType { get; set; }

    public string RamdiskName { get; set; } = string.Empty;

    public uint[] RamdiskBoardId { get; set; } = new uint[16];
}