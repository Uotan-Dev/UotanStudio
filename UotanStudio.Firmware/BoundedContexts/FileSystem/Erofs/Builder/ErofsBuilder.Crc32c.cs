namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed partial class ErofsBuilder
{

    private static readonly uint[] ErofsCrc32cTable = InitErofsCrc32cTable();

    /// <summary>
    /// Seed used by the Linux kernel's EROFS superblock checksum (fs/erofs/super.c erofs_superblock_csum_verify) and by erofs-utils.
    /// <para>Linux 内核 EROFS 超级块校验和使用的种子（fs/erofs/super.c erofs_superblock_csum_verify），erofs-utils 亦同。</para>
    /// </summary>
    private const uint ErofsSuperblockChecksumSeed = 0x5045B54Au;

    /// <summary>
    /// Computes the CRC32c checksum of the given data (crc32c_le convention: seed, no final XOR).
    /// <para>计算数据的 CRC32c 校验值（crc32c_le 约定：以 seed 为初值，无最终异或）。</para>
    /// </summary>
    private static uint ComputeCrc32C(ReadOnlySpan<byte> data, uint seed = 0xFFFF_FFFF)
    {
        uint crc = seed;
        foreach (var b in data)
        {
            crc = ErofsCrc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }


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