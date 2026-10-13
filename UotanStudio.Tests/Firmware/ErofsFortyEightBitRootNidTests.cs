using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards how a <c>48bit</c> superblock resolves its root inode number. erofs-utils leaves the wide
/// <c>rootnid_8b</c> field zero whenever the root number already fits the 16-bit legacy field, and its
/// reader therefore prefers the wide field only when that field is non-zero
/// (<c>lib/super.c</c>). Reading the wide field unconditionally turns such an image into an empty tree.
/// <para>守护 <c>48bit</c> 超级块解析根 inode 编号的方式。erofs-utils 在根编号本就能装进 16 位旧字段时会把
/// 64 位的 <c>rootnid_8b</c> 留为零，因此其读取方仅在该字段非零时才优先使用它（<c>lib/super.c</c>）。
/// 无条件读取宽字段会把这类镜像解析成空树。</para>
/// </summary>
public sealed class ErofsFortyEightBitRootNidTests
{
    private const int BlockSize = 4096;

    private const int RootNid8bOffset = ErofsSuperblock.SuperblockOffset + 112;

    [Fact]
    public void FromImage_48BitWithZeroWideRootNid_FallsBackToTheLegacyField()
    {
        var image = BuildFortyEightBitImage();
        var rootNid = EmulateMkfsLayout(image);

        var parsed = ErofsSuperblock.FromImage(image);

        Assert.True(parsed.Has48Bit);
        Assert.Equal(0UL, parsed.RootNid8b);
        Assert.Equal(rootNid, parsed.RootNid64);
    }

    [Fact]
    public void FromImage_48BitWithWideRootNid_PrefersTheWideField()
    {
        var image = BuildFortyEightBitImage();
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(RootNid8bOffset), 77UL);

        var parsed = ErofsSuperblock.FromImage(image);

        Assert.True(parsed.Has48Bit);
        Assert.Equal(77UL, parsed.RootNid8b);
        Assert.Equal(77UL, parsed.RootNid64);
    }

    [Fact]
    public void FromImage_WithoutFortyEightBit_ReadsTheLegacyFieldOnly()
    {
        var image = BuildImage(configure: null);
        var legacy = BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(ErofsSuperblock.SuperblockOffset + 14));

        var parsed = ErofsSuperblock.FromImage(image);

        Assert.False(parsed.Has48Bit);
        Assert.NotEqual(0, legacy);
        Assert.Equal((ulong)legacy, parsed.RootNid64);
    }

    [Fact]
    public void Build_FortyEightBitImage_RootResolvesAndTreeIsReadable()
    {
        var image = BuildFortyEightBitImage();

        var parsed = ErofsSuperblock.FromImage(image);
        using var parser = new ErofsParser(image);

        Assert.True(parsed.Has48Bit);
        Assert.NotEqual(0UL, parsed.RootNid64);
        Assert.Equal(parsed.RootNid64, parser.RootNid);
        Assert.Contains(parser.EnumerateFiles(), file => file.Path.EndsWith("data.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void FromImage_ZeroWideRootNidUnder48Bit_TreeIsStillReadable()
    {
        var image = BuildFortyEightBitImage();
        EmulateMkfsLayout(image);

        using var parser = new ErofsParser(image);

        Assert.NotEqual(0UL, parser.RootNid);
        Assert.Contains(parser.EnumerateFiles(), file => file.Path.EndsWith("data.bin", StringComparison.Ordinal));
    }

    private static byte[] BuildFortyEightBitImage()
        => BuildImage(options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

    private static ulong EmulateMkfsLayout(byte[] image)
    {
        var rootNid = ErofsSuperblock.FromImage(image).RootNid8b;
        Assert.NotEqual(0UL, rootNid);
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan(ErofsSuperblock.SuperblockOffset + 14), (ushort)rootNid);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(RootNid8bOffset), 0UL);
        return rootNid;
    }

    private static byte[] BuildImage(Action<ErofsOptions>? configure)
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray());
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = BlockSize,
            SuperblockChecksum = false,
            Uuid = new byte[16],
            BuildTime = 1_700_000_000UL,
        };
        configure?.Invoke(options);
        return new ErofsBuilder().Build(source.Path, options);
    }
}