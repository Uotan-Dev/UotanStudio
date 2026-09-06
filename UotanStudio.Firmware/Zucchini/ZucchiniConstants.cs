namespace UotanStudio.Firmware.Zucchini;

public static class ZucchiniConstants
{
    public const uint Magic = 0x0350635A;

    public const uint Version = 1;

    public const int HeaderByteSize = 7 * sizeof(uint);

    public const int ElementHeaderByteSize = 10 * sizeof(uint);
}
