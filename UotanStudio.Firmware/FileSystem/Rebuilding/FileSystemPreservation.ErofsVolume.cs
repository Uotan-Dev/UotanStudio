using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Checksums;
using UotanStudio.Firmware.FileSystem.Erofs.Models;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.
/// <para>保留源镜像语义，并把编辑后的宿主文件树解析为使用原始或继承 Linux 属性的文件系统构建器。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    private static void PatchErofsVolume(FileStream image, string volume)
    {
        image.Position = ErofsSuperblock.SuperblockOffset;
        var header = new byte[128];
        image.ReadExactly(header);
        var superblock = ErofsSuperblock.FromBytes(header);
        if (superblock.BlockSize is < 512 or > 65536
            || checked((ulong)image.Length) != checked(superblock.BlockCount64 * (ulong)superblock.BlockSize))
            throw new FileSystemUnsupportedFeatureException("Cannot change an EROFS volume with additional image tails or unsupported block geometry.");
        var region = new byte[superblock.BlockSize > ErofsSuperblock.SuperblockOffset
            ? superblock.BlockSize - ErofsSuperblock.SuperblockOffset : superblock.BlockSize];
        image.Position = ErofsSuperblock.SuperblockOffset;
        image.ReadExactly(region);
        region.AsSpan(64, 16).Clear();
        Encoding.UTF8.GetBytes(volume, region.AsSpan(64, 16));
        if (superblock.HasCompatChecksum)
        {
            region.AsSpan(4, 4).Clear();
            var crc = Crc32c.Compute(region, 0xFFFF_FFFFu);
            BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(4, 4), crc);
        }
        image.Position = ErofsSuperblock.SuperblockOffset;
        image.Write(region);
    }
}