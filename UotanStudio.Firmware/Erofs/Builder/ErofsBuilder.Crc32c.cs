namespace UotanStudio.Firmware.Erofs;

public sealed partial class ErofsBuilder
{
    // CRC32c lookup table (reflected, poly 0x82F63B78) — matches erofs-utils crc32c_le.
    private static readonly uint[] ErofsCrc32cTable = InitErofsCrc32cTable();

    /// <summary>
    /// 计算数据的 CRC32c 校验值（crc32c_le 约定：初始 ~0，无最终异或）。
    /// Computes the CRC32c checksum of the given data (crc32c_le convention: init ~0, no final XOR).
    /// </summary>
    private static uint ComputeCrc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFF;
        foreach (var b in data)
        {
            crc = ErofsCrc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    // Build reflected CRC32c table with polynomial 0x82F63B78.
    private static uint[] InitErofsCrc32cTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78 : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
