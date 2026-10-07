using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Checksums;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Ext4.Builder;

/// <summary>
/// Partial declaration implementing inode timestamp/identity writing and external xattr block planning.
/// <para>实现 inode 时间戳/身份属性写入与外部 xattr 块规划的局部声明。</para>
/// </summary>
public sealed partial class Ext4Builder
{
    private static void WriteSourceMetadata(byte[] inode, FileSystemInodeMetadata? metadata)
    {
        if (metadata is null)
        {
            return;
        }
        metadata.Validate();
        WriteTimestamp(inode, metadata.AccessTime, 8, 140);
        WriteTimestamp(inode, metadata.ChangeTime, 12, 132);
        WriteTimestamp(inode, metadata.ModificationTime, 16, 136);
        WriteTimestamp(inode, metadata.BirthTime, 144, 148);
        WriteU32(inode, 100, metadata.Generation);
        WriteU32(inode, 36, (uint)(metadata.Version ?? 0));
        if (inode.Length >= 160)
        {
            WriteU32(inode, 152, (uint)((metadata.Version ?? 0) >> 32));
            WriteU32(inode, 156, metadata.ProjectId ?? 0);
        }
        else if (metadata.ProjectId is not null || metadata.Version > uint.MaxValue)
        {
            throw new FileSystemUnsupportedFeatureException("EXT4 inode cannot represent the preserved extended identity.");
        }
    }

    private static void WriteTimestamp(byte[] inode, FileSystemTimestamp? timestamp, int offset, int extraOffset)
    {
        if (timestamp is null)
        {
            return;
        }
        if (offset + 4 > inode.Length)
        {
            throw new FileSystemUnsupportedFeatureException("EXT4 inode cannot represent the preserved timestamp.");
        }
        var low = unchecked((int)timestamp.Seconds);
        var epoch = (timestamp.Seconds - low) >> 32;
        var extended = extraOffset + 4 <= inode.Length && inode.Length >= 128 + Ext4ExtraIsize;
        if (epoch is < 0 or > 3 || !extended && (epoch != 0 || timestamp.Nanoseconds != 0))
        {
            throw new FileSystemUnsupportedFeatureException("EXT4 timestamp is outside the selected inode's range.");
        }
        BinaryPrimitives.WriteInt32LittleEndian(inode.AsSpan(offset), low);
        if (extended)
        {
            WriteU32(inode, extraOffset, (timestamp.Nanoseconds << 2) | (uint)epoch);
        }
    }

    private static uint PlanXattrs(List<Node> nodes, int inodeSize, int blockSize, uint maxInode, byte[] uuid)
    {
        var owners = nodes.ToArray();
        foreach (var node in owners)
        {
            if (node.Xattrs is not { Count: > 0 } attributes)
            {
                continue;
            }
            var length = 4L;
            foreach (var (name, value) in attributes)
            {
                if (!TryMapXattrName(name, out _, out var suffix) || Encoding.UTF8.GetByteCount(suffix) > FileSystemNameLimits.MaximumNameBytes)
                {
                    throw new FileSystemUnsupportedFeatureException($"Unsupported EXT4 xattr name: {name}");
                }
                length += ((16 + Encoding.UTF8.GetByteCount(suffix) + 3) & ~3) + ((value.LongLength + 3) & ~3L);
            }
            if (!node.IsInlineData && length <= inodeSize - 128 - Ext4ExtraIsize - 4)
            {
                continue;
            }
            var externalValues = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var pair in attributes.OrderByDescending(pair => pair.Value.Length))
            {
                if (length <= blockSize - 32)
                {
                    break;
                }
                if (pair.Value.Length is 0 or > 65536)
                {
                    throw new FileSystemCorruptedException("EXT4 EA inode value is empty or exceeds 64 KiB.");
                }
                var inode = new Node
                {
                    Name = "",
                    RelativePath = "",
                    FullPath = "",
                    IsDirectory = false,
                    IsSymlink = false,
                    Mode = 0x8180,
                    Size = pair.Value.Length,
                    AttributeValue = pair.Value,
                    Inode = checked(Math.Max(maxInode, FirstUsableInode - 1) + 1),
                    SemanticFlags = 0x0020_0000
                };
                maxInode = inode.Inode;
                nodes.Add(inode);
                externalValues.Add(pair.Key, inode);
                node.AttributeValueBlocks += (pair.Value.LongLength + blockSize - 1) / blockSize;
                length -= (pair.Value.LongLength + 3) & ~3L;
            }
            if (length > blockSize - 32)
            {
                throw new FileSystemCorruptedException("EXT4 xattr names exceed a single external block.");
            }
            var block = new byte[blockSize];
            WriteU32(block, 0, Ext4Xattr.Magic);
            WriteU32(block, 4, 1);
            WriteU32(block, 8, 1);
            var position = 32;
            var valuePosition = blockSize;
            foreach (var pair in attributes.Select(pair =>
                     {
                         TryMapXattrName(pair.Key, out var index, out var name);
                         return (Index: index, Name: Encoding.UTF8.GetBytes(name), Value: pair.Value, Inode: externalValues.GetValueOrDefault(pair.Key));
                     }).OrderBy(pair => pair.Index).ThenBy(pair => pair.Name.Length)
                     .ThenBy(pair => Convert.ToHexString(pair.Name), StringComparer.Ordinal))
            {
                if (pair.Inode is null)
                {
                    valuePosition -= (pair.Value.Length + 3) & ~3;
                }
                block[position] = (byte)pair.Name.Length;
                block[position + 1] = pair.Index;
                WriteU16(block, position + 2, pair.Inode is null ? checked((ushort)valuePosition) : (ushort)0);
                WriteU32(block, position + 4, pair.Inode?.Inode ?? 0);
                WriteU32(block, position + 8, (uint)pair.Value.Length);
                pair.Name.CopyTo(block, position + 16);
                var hash = 0u;
                foreach (var character in pair.Name)
                {
                    hash = (hash << 5) ^ (hash >> 27) ^ character;
                }
                if (pair.Inode is null)
                {
                    pair.Value.CopyTo(block, valuePosition);
                    for (var offset = 0; offset < pair.Value.Length; offset += 4)
                    {
                        hash = (hash << 16) ^ (hash >> 16) ^ BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(valuePosition + offset));
                    }
                }
                else
                {
                    hash = (hash << 16) ^ (hash >> 16) ^ ComputeCrc32(pair.Value, ComputeCrc32(uuid, Crc32c.InitialSeed));
                }
                WriteU32(block, position + 12, hash);
                position += (16 + pair.Name.Length + 3) & ~3;
            }
            node.ExternalXattrs = block;
        }
        return maxInode;
    }
}