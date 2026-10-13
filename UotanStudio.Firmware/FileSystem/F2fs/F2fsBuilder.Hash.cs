namespace UotanStudio.Firmware.FileSystem.F2fs;

public sealed partial class F2fsBuilder
{
    private static uint Crc32(uint crc, ReadOnlySpan<byte> data) => UotanStudio.Hash.Crc32.Crc32.Compute(data, crc);
}