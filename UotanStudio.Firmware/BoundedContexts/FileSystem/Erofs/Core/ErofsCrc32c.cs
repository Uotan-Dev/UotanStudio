using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

/// <summary>
/// CRC32c helpers shared by the EROFS parser and builder, matching the Linux kernel's
/// crc32c_le convention (register seed, no final XOR).
/// <para>解析器与打包器共享的 CRC32c 工具，与内核 crc32c_le 约定一致（寄存器 seed，无最终异或）。</para>
/// </summary>
internal static class ErofsCrc32c
{
    /// <summary>
    /// Seed used by the Linux kernel's EROFS superblock checksum (fs/erofs/super.c erofs_superblock_csum_verify) and by erofs-utils.
    /// <para>Linux 内核 EROFS 超级块校验和使用的种子（fs/erofs/super.c erofs_superblock_csum_verify），erofs-utils 亦同。</para>
    /// </summary>
    public const uint SuperblockChecksumSeed = 0x5045B54Au;

    private static readonly uint[] Table = InitTable();

    /// <summary>
    /// Computes the CRC32c checksum of the given data (crc32c_le convention: seed, no final XOR).
    /// <para>计算数据的 CRC32c 校验值（crc32c_le 约定：以 seed 为初值，无最终异或）。</para>
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0xFFFF_FFFF)
    {
        uint crc = seed;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>
    /// Verifies the EROFS superblock checksum over the image window [1024, 1024+windowLength),
    /// skipping the magic and checksum fields (first 8 bytes), mirroring fs/erofs/super.c.
    /// <para>校验镜像窗口 [1024, 1024+windowLength) 的超级块校验和，跳过魔数与校验和字段（前 8 字节），与 fs/erofs/super.c 一致。</para>
    /// </summary>
    public static uint VerifySuperblock(ReadOnlySpan<byte> window)
    {
        if (window.Length < 8)
        {
            throw new ArgumentException("Superblock checksum window is too small.", nameof(window));
        }

        return Compute(window[8..], SuperblockChecksumSeed);
    }

    private static uint[] InitTable()
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