using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// ext4 解析器不兼容特性掩码测试：真实 mke2fs 镜像常见的 FILETYPE/LARGEDIR/INLINE_DATA 等
/// incompat 位必须被接受，仅真正无法读取的布局位（META_BG/JOURNAL_DEV）拒绝。
/// <para>ext4 parser incompat feature mask tests: incompat bits common in real mke2fs images
/// (FILETYPE/LARGEDIR/INLINE_DATA etc.) must be accepted; only bits the parser cannot read
/// (META_BG/JOURNAL_DEV) are rejected.</para>
/// </summary>
public class Ext4IncompatFeatureTests
{
    private const int FeatureCompatOffset = 1024 + 92;
    private const int FeatureIncompatOffset = 1024 + 96;
    private const int FeatureRoCompatOffset = 1024 + 100;
    private const uint FlexBgBit = 0x0200;
    private const uint EaInodeBit = 0x0400;
    private const uint CasefoldBit = 0x20000;
    private const uint MetadataChecksumBit = 0x0400;
    private const uint ReadOnlyBit = 0x1000;
    private const uint SharedBlocksBit = 0x4000;

    [Fact]
    public void Parser_AcceptsFiletypeBit()
    {
        var image = BuildImage();
        SetIncompat(image, FeatureIncompat.Filetype);
        Parses(image);
    }

    [Fact]
    public void Parser_AcceptsRealWorldIncompatMask()
    {
        var image = BuildImage();
        SetIncompat(image, FeatureIncompat.Filetype | FeatureIncompat.Extents | FeatureIncompat.Bits64
            | FeatureIncompat.FlexBg | FeatureIncompat.LargeDir | FeatureIncompat.InlineData);
        SetRoCompat(image, 0x0400u);
        Parses(image);
    }

    [Fact]
    public void Parser_StillRejectsMetaBg()
    {
        var image = BuildImage();
        SetIncompat(image, FeatureIncompat.MetaBg);
        Assert.Throws<FileSystemUnsupportedFeatureException>(() => Parse(image));
    }

    [Fact]
    public void Parser_StillRejectsUnknownHighBits()
    {
        var image = BuildImage();
        SetIncompat(image, 0x8000_0000u);
        Assert.Throws<FileSystemUnsupportedFeatureException>(() => Parse(image));
    }

    [Fact]
    public void Build_MinimalLayout_WritesExactFeatureWords()
    {
        var image = BuildImage(new Ext4Options { IncludeJournal = false, EnableMetadataCsum = false });

        AssertFeatureWords(image, 0x0038, 0x02C2, 0x006B);
        Assert.Equal(FlexBgBit, ReadFeatureWord(image, FeatureIncompatOffset) & FlexBgBit);
        Assert.Equal(0u, ReadFeatureWord(image, FeatureIncompatOffset) & EaInodeBit);
    }

    [Fact]
    public void Build_WithJournal_WritesExactFeatureWords()
    {
        var image = BuildImage(new Ext4Options { IncludeJournal = true, EnableMetadataCsum = false });

        AssertFeatureWords(image, 0x003C, 0x02C2, 0x006B);
        Assert.Equal(FlexBgBit, ReadFeatureWord(image, FeatureIncompatOffset) & FlexBgBit);
    }

    [Fact]
    public void Build_WithCasefold_WritesExactFeatureWordsWithoutEaInode()
    {
        var image = BuildImage(new Ext4Options
        {
            IncludeJournal = false,
            EnableMetadataCsum = false,
            CasefoldEncoding = 1,
        });

        AssertFeatureWords(image, 0x0038, 0x02C2 | CasefoldBit, 0x006B);
        Assert.Equal(CasefoldBit, ReadFeatureWord(image, FeatureIncompatOffset) & CasefoldBit);
        Assert.Equal(FlexBgBit, ReadFeatureWord(image, FeatureIncompatOffset) & FlexBgBit);
        Assert.Equal(0u, ReadFeatureWord(image, FeatureIncompatOffset) & EaInodeBit);
    }

    [Fact]
    public void Build_WithMetadataChecksum_WritesExactFeatureWords()
    {
        var image = BuildImage(new Ext4Options { IncludeJournal = false, EnableMetadataCsum = true });

        AssertFeatureWords(image, 0x0038, 0x02C2, 0x006B | MetadataChecksumBit);
        Assert.Equal(0u, ReadFeatureWord(image, FeatureIncompatOffset) & (MetadataChecksumBit | CasefoldBit));
    }

    [Fact]
    public void Build_ReadOnly_WritesExactFeatureWords()
    {
        var image = BuildImage(new Ext4Options
        {
            IncludeJournal = false,
            EnableMetadataCsum = false,
            ReadOnly = true,
        });

        AssertFeatureWords(image, 0x0038, 0x02C2, 0x006B | ReadOnlyBit);
        Assert.Equal(0u, ReadFeatureWord(image, FeatureIncompatOffset) & (ReadOnlyBit | SharedBlocksBit));
    }

    [Fact]
    public void Build_WithSharedDuplicateBlocks_WritesExactFeatureWords()
    {
        var image = BuildImage(new Ext4Options
        {
            IncludeJournal = false,
            EnableMetadataCsum = false,
            ReadOnly = true,
            BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
        }, duplicateBlocks: true);

        AssertFeatureWords(image, 0x0038, 0x02C2, 0x006B | ReadOnlyBit | SharedBlocksBit);
        Assert.Equal(0u, ReadFeatureWord(image, FeatureIncompatOffset) & (EaInodeBit | MetadataChecksumBit));
        Assert.Equal(FlexBgBit, ReadFeatureWord(image, FeatureIncompatOffset) & FlexBgBit);
    }

    private static void AssertFeatureWords(byte[] image, uint compat, uint incompat, uint roCompat)
    {
        Assert.Equal(compat, ReadFeatureWord(image, FeatureCompatOffset));
        Assert.Equal(incompat, ReadFeatureWord(image, FeatureIncompatOffset));
        Assert.Equal(roCompat, ReadFeatureWord(image, FeatureRoCompatOffset));
    }

    private static uint ReadFeatureWord(byte[] image, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));

    private static byte[] BuildImage(Ext4Options options, bool duplicateBlocks = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-ext4-word-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            if (duplicateBlocks)
            {
                var payload = CreateDeterministicBlock();
                File.WriteAllBytes(Path.Combine(root, "first.bin"), payload);
                File.WriteAllBytes(Path.Combine(root, "second.bin"), payload);
            }
            else
            {
                File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            }

            using var image = new MemoryStream();
            Ext4Builder.Build(root, image, options);
            return image.ToArray();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateDeterministicBlock()
    {
        var payload = new byte[4096];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }

    private static byte[] BuildImage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-ext4-feat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            return Ext4Builder.Build(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SetIncompat(byte[] image, uint bits)
    {
        var current = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(FeatureIncompatOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(FeatureIncompatOffset, 4), current | bits);
    }

    private static void SetRoCompat(byte[] image, uint bits)
    {
        var current = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(FeatureRoCompatOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(FeatureRoCompatOffset, 4), current | bits);
    }

    private static Ext4Parser Parse(byte[] image) => new(new MemoryStream(image, writable: false));

    private static void Parses(byte[] image)
    {
        using var parser = Parse(image);
        var files = parser.ListFiles().Select(static e => e.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("hello.txt", files);
    }

    private static class FeatureIncompat
    {
        public const uint Filetype = 0x0002;
        public const uint MetaBg = 0x0010;
        public const uint Extents = 0x0040;
        public const uint Bits64 = 0x0080;
        public const uint FlexBg = 0x0200;
        public const uint LargeDir = 0x4000;
        public const uint InlineData = 0x8000;
    }
}