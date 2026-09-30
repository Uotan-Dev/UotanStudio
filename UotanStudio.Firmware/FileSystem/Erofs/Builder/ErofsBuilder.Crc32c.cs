using UotanStudio.Firmware.Core.Checksums;
namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that carries the CRC32c helper used by superblock and metadata checksums.
/// <para>EROFS 构建器的组成部分，提供超级块与元数据校验和所用的 CRC32c 计算入口。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    /// <summary>Superblock CRC32c seed value used by the Linux kernel's EROFS superblock checksum and erofs-utils.
    /// <para>超级块 CRC32c 种子值，Linux 内核 EROFS 超级块校验与 erofs-utils 均使用该值。</para></summary>
    private const uint ErofsSuperblockChecksumSeed = 0x5045B54A;

    private static uint ComputeCrc32C(ReadOnlySpan<byte> data, uint seed = Crc32c.InitialSeed) => Crc32c.Compute(data, seed);
}