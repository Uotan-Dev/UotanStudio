using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Checksums;
namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;

public sealed partial class ErofsBuilder
{
    /// <summary>Temporary alias until the in-progress CRC32c refactor updates ErofsBuilder.cs to ErofsCrc32c.SuperblockChecksumSeed.</summary>
    private const uint ErofsSuperblockChecksumSeed = 0x5045B54A;

    private static uint ComputeCrc32C(ReadOnlySpan<byte> data, uint seed = 0xFFFF_FFFF) => ErofsCrc32c.Compute(data, seed);
}