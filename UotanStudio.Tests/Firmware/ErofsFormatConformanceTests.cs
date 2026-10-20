using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsFormatConformanceTests
{
    private const int SuperblockFeatureCompatOffset = ErofsSuperblock.SuperblockOffset + 8;

    [Fact]
    public void Build_IsharePrefix_WritesIndexWithinPrefixCount()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x41, 3000).ToArray());
        const string prefix = "erofs.fingerprint.v1";

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            SuperblockChecksum = false,
            IshareXattrs = true,
            IshareXattrPrefix = prefix,
        });

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.True(superblock.XattrPrefixCount > 0, "a configured fingerprint prefix must register a table record.");
        Assert.True(superblock.IshareXattrPrefixId < superblock.XattrPrefixCount,
            $"the on-disk id {superblock.IshareXattrPrefixId} must be a plain index below the prefix count.");
        Assert.Equal(0, superblock.IshareXattrPrefixId & ErofsXattr.LongPrefixFlag);
        Assert.True(superblock.HasCompatIshareXattrs,
            "upstream sets ishare_xattrs inside erofs_xattr_set_ishare_prefix, so a digest build must publish it.");
        superblock.Validate();
    }

    [Fact]
    public void Build_IsharePrefixWithoutCompatBit_IsNotResolvedAsAName()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x42, 3000).ToArray());
        const string prefix = "erofs.fingerprint.v1";

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            SuperblockChecksum = false,
            IshareXattrs = true,
            IshareXattrPrefix = prefix,
            SourceXattrPrefixes = [(0, "other.prefix.")],
        });

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.NotEqual(0, superblock.IshareXattrPrefixId);

        var patched = (byte[])image.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(SuperblockFeatureCompatOffset, 4),
            superblock.FeatureCompat & ~ErofsSuperblock.CompatIshareXattrs);

        var patchedSuperblock = ErofsSuperblock.FromImage(patched);
        Assert.False(patchedSuperblock.HasCompatIshareXattrs);
        Assert.NotEqual(0, patchedSuperblock.IshareXattrPrefixId);
        patchedSuperblock.Validate();

        using var parser = new ErofsParser(patched);
        var restored = parser.ReadRebuildOptions();
        Assert.True(string.IsNullOrEmpty(restored.IshareXattrPrefix),
            "an id carried without the compat bit names nothing and must not be replayed as a prefix.");
    }

    [Fact]
    public void Build_NamesSortingBeforeDot_ProduceMonotonicDirentOrder()
    {
        using var source = new TempDirectory();
        source.WriteBytes("!a.txt", [0x01]);
        source.WriteBytes("-b.txt", [0x02]);
        source.WriteBytes("$c.txt", [0x03]);
        source.WriteBytes("z.txt", [0x04]);

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            SuperblockChecksum = false,
            Compress = false,
        });

        var names = ReadRootDirectoryNames(image);
        var decoded = names.Select(Encoding.UTF8.GetString).ToArray();
        Assert.Equal(new[] { "!a.txt", "$c.txt", "-b.txt", ".", "..", "z.txt" }, decoded);
        AssertDirentNameOrder(names);
    }

    [Fact]
    public void Build_MultiBlockDirectory_SortingBeforeDot_StaysMonotonic()
    {
        using var source = new TempDirectory();
        for (var index = 0; index < 420; index++)
        {
            source.WriteBytes($"$f{index:D3}.txt", [0x05]);
        }

        source.WriteBytes("zz.txt", [0x06]);

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            SuperblockChecksum = false,
            Compress = false,
        });

        var superblock = ErofsSuperblock.FromImage(image);
        var inode = ErofsInode.FromBytes(
            image.AsSpan((int)superblock.GetInodeOffset(superblock.RootNid64)),
            superblock.RootNid64,
            superblock.BlockSizeBits);
        Assert.NotEqual(ErofsDataLayout.Inline, inode.DataLayout);

        var names = ReadRootDirectoryNames(image);
        Assert.Equal(423, names.Count);
        AssertDirentNameOrder(names);
    }

    [Fact]
    public void DirentNameOrder_LegacyTwoStepSequence_IsRejected()
    {
        var legacy = new List<byte[]>
        {
            "."u8.ToArray(),
            ".."u8.ToArray(),
            "!a.txt"u8.ToArray(),
            "$c.txt"u8.ToArray(),
            "-b.txt"u8.ToArray(),
            "z.txt"u8.ToArray(),
        };
        Assert.False(IsDirentNameOrder(legacy));
    }

    [Fact]
    public async Task Build_FormatHandlerUnpackPack_ExcludesExtractionMetadata()
    {
        using var source = new TempDirectory();
        source.WriteBytes("payload.txt", Encoding.ASCII.GetBytes("payload"));
        var imagePath = Path.Combine(source.Path, "source.img");
        new ErofsBuilder().Build(source.Path, imagePath, new ErofsOptions
        {
            SuperblockChecksum = false,
            Compress = false,
        });

        var extracted = Path.Combine(source.Path, "extracted");
        var handler = new ErofsFormatHandler();
        var unpacked = await handler.UnpackAsync(imagePath, extracted, null, TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);

        var rebuilt = Path.Combine(source.Path, "rebuilt.img");
        var packed = await handler.PackAsync(extracted, rebuilt, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        using var parser = new ErofsParser(rebuilt);
        var paths = parser.EnumerateEntries().Select(entry => entry.Path).ToArray();
        Assert.Contains("payload.txt", paths);
        Assert.DoesNotContain(paths, path => IsExtractionMetadata(Path.GetFileName(path)));
    }

    private static bool IsExtractionMetadata(string fileName)
        => fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_fs_config", StringComparison.Ordinal)
            || fileName.EndsWith("_file_contexts", StringComparison.Ordinal)
            || fileName.EndsWith("_info", StringComparison.Ordinal);

    private static List<byte[]> ReadRootDirectoryNames(byte[] image)
    {
        var superblock = ErofsSuperblock.FromImage(image);
        var inodeOffset = superblock.GetInodeOffset(superblock.RootNid64);
        var inode = ErofsInode.FromBytes(
            image.AsSpan((int)inodeOffset), superblock.RootNid64, superblock.BlockSizeBits);
        var blockSize = superblock.BlockSize;
        if (inode.DataLayout == ErofsDataLayout.Inline)
        {
            var start = (int)inodeOffset + inode.InlineDataOffset;
            return ReadBlockNames(image.AsSpan(start, (int)inode.Size));
        }

        var names = new List<byte[]>();
        var offset = checked((int)((long)inode.AddressField() * blockSize));
        var remaining = (int)inode.Size;
        while (remaining > 0)
        {
            var length = Math.Min(blockSize, remaining);
            names.AddRange(ReadBlockNames(image.AsSpan(offset, length)));
            offset += blockSize;
            remaining -= length;
        }

        return names;
    }

    private static List<byte[]> ReadBlockNames(ReadOnlySpan<byte> block)
    {
        var names = new List<byte[]>();
        if (block.Length < ErofsDirectoryEntry.DirentSize)
        {
            return names;
        }

        var nameAreaStart = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
        var count = nameAreaStart / ErofsDirectoryEntry.DirentSize;
        for (var index = 0; index < count; index++)
        {
            var nameOff = BinaryPrimitives.ReadUInt16LittleEndian(
                block.Slice(index * ErofsDirectoryEntry.DirentSize + 8, 2));
            int nameEnd;
            if (index + 1 < count)
            {
                nameEnd = BinaryPrimitives.ReadUInt16LittleEndian(
                    block.Slice((index + 1) * ErofsDirectoryEntry.DirentSize + 8, 2));
            }
            else
            {
                nameEnd = nameOff;
                while (nameEnd < block.Length && block[nameEnd] != 0)
                {
                    nameEnd++;
                }
            }

            names.Add(block.Slice(nameOff, nameEnd - nameOff).ToArray());
        }

        return names;
    }

    private static void AssertDirentNameOrder(IReadOnlyList<byte[]> names)
        => Assert.True(IsDirentNameOrder(names),
            string.Join(" ", names.Select(Encoding.UTF8.GetString)));

    private static bool IsDirentNameOrder(IReadOnlyList<byte[]> names)
    {
        for (var index = 1; index < names.Count; index++)
        {
            var previous = names[index - 1];
            var current = names[index];
            var shared = Math.Min(previous.Length, current.Length);
            var comparison = 0;
            for (var position = 0; position < shared; position++)
            {
                comparison = previous[position].CompareTo(current[position]);
                if (comparison != 0)
                {
                    break;
                }
            }

            if (comparison > 0 || (comparison == 0 && previous.Length >= current.Length))
            {
                return false;
            }
        }

        return true;
    }
}