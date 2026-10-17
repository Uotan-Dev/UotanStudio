using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;

namespace UotanStudio.Tests.Firmware;

internal static class ErofsInodeNumberFixture
{
    /// <summary>
    /// Overwrites every inode record's <c>i_ino</c> with a distinct non-zero number and returns those numbers
    /// by image path. The image must not declare the superblock checksum feature, because patching an inode
    /// invalidates that CRC and the next parse would reject the image.
    /// <para>把每条 inode 记录的 <c>i_ino</c> 覆写为互不相同的非零编号，并按镜像路径返回这些编号。镜像不得
    /// 声明超级块校验和特性，因为改写 inode 会使该 CRC 失效，下一次解析会拒绝该镜像。</para>
    /// </summary>
    /// <param name="image">Image whose inode records are stamped in place.
    /// <para>就地写入编号的 inode 记录所在镜像。</para></param>
    /// <returns>The stamped number per image path. <para>按镜像路径记录的编号。</para></returns>
    /// <exception cref="InvalidOperationException">The image declares the superblock checksum feature, so
    /// stamping it would leave a stale CRC.
    /// <para>镜像声明了超级块校验和特性，改写后会留下失效的 CRC。</para></exception>
    internal static Dictionary<string, uint> Stamp(byte[] image)
    {
        if (ErofsSuperblock.FromImage(image).HasCompatChecksum)
        {
            throw new InvalidOperationException(
                "The image declares the superblock checksum feature, so stamping i_ino would invalidate its CRC. "
                + "Build the source image with SuperblockChecksum = false.");
        }

        var offsets = ReadRecordOffsets(image);
        var numbers = new Dictionary<string, uint>(offsets.Count, StringComparer.Ordinal);
        foreach (var (path, offset) in offsets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var number = 0x1000u + (uint)numbers.Count;
            numbers[path] = number;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 20, 4), number);
        }

        return numbers;
    }

    internal static Dictionary<string, uint> Read(byte[] image)
    {
        var numbers = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (path, offset) in ReadRecordOffsets(image))
        {
            numbers[path] = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 20, 4));
        }

        return numbers;
    }

    private static Dictionary<string, int> ReadRecordOffsets(byte[] image)
    {
        var superblock = ErofsSuperblock.FromImage(image);
        using var parser = new ErofsParser(image);
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in parser.EnumerateEntries())
        {
            offsets[entry.Path] = checked((int)superblock.GetInodeOffset(entry.Nid));
        }

        return offsets;
    }
}