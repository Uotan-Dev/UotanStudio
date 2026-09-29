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
    private const int FeatureIncompatOffset = 1024 + 96;

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
        var current = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 100, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 100, 4), current | bits);
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