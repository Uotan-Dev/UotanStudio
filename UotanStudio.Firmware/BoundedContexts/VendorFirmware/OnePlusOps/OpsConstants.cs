namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps;

public static class OpsConstants
{
    public const string OPS_MAGIC = "OPS\0";

    public const int HEADER_SIZE = 52;

    public const int ENTRY_HEADER_SIZE = 32;

    public const int ENTRY_NAME_SIZE = 20;

    public const int AES_BLOCK_SIZE = 16;

    public const int SECTOR_SIZE = 0x200;

    public const int FOOTER_SIZE = 0x200;
}
