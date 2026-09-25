using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Checksums;
namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that carries the CRC32c helper used by superblock and metadata checksums.
/// <para>EROFS 构建器的组成部分，提供超级块与元数据校验和所用的 CRC32c 计算入口。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    /// <summary>Superblock CRC32c seed value, mirroring <see cref="ErofsCrc32c.SuperblockChecksumSeed"/>.
    /// <para>超级块 CRC32c 种子值，与 <see cref="ErofsCrc32c.SuperblockChecksumSeed"/> 保持一致。</para></summary>
    private const uint ErofsSuperblockChecksumSeed = 0x5045B54A;

    private static uint ComputeCrc32C(ReadOnlySpan<byte> data, uint seed = 0xFFFF_FFFF) => ErofsCrc32c.Compute(data, seed);
}