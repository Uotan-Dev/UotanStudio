using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Covers the host-metadata paths of the EROFS writer that run without an edit plan: the device identifier
/// written into <c>i_rdev</c>, the record width a timestamp forces, and the superblock slot a small-block
/// multi-device image must never overwrite.
/// <para>覆盖 EROFS 写入方在没有编辑计划时执行的宿主元数据路径：写入 <c>i_rdev</c> 的设备号、
/// 时间戳所要求的记录宽度，以及小块设备多设备镜像绝不得覆写的超级块槽位。</para>
/// </summary>
public class ErofsUnixMetadataCollectionTests
{
    private static ulong MakeHostDeviceId(ulong major, ulong minor)
        => ((major & 0xfff) << 8) | (minor & 0xff) | ((minor & 0xfff00) << 12) | ((major & 0xffff0000) << 32);

    [Theory]
    [InlineData(138u, 5u)]
    [InlineData(1u, 300u)]
    [InlineData(259u, 65540u)]
    [InlineData(7u, 1048575u)]
    public void EncodeUnixDeviceId_KnownHostDevice_MatchesErofsNewEncodeDev(uint major, uint minor)
    {
        var host = MakeHostDeviceId(major, minor);
        var onDisk = (minor & 0xff) | (major << 8) | ((minor & ~0xffu) << 12);

        Assert.Equal(onDisk, ErofsBuilder.EncodeUnixDeviceId(host));
    }

    [Fact]
    public void EncodeUnixDeviceId_MajorBeyondLowTwelveBits_KeepsMajorInsteadOfTruncatingHostField()
    {
        const uint major = 0x10170;
        var host = MakeHostDeviceId(major, 3);

        Assert.Equal(3u | (major << 8), ErofsBuilder.EncodeUnixDeviceId(host));
        Assert.NotEqual((uint)host, ErofsBuilder.EncodeUnixDeviceId(host));
    }

    [Theory]
    [InlineData(1_700_000_000UL, 1_700_000_000UL, 0u, false)]
    [InlineData(1_700_000_000UL, 1_700_000_000UL, 500u, true)]
    [InlineData(1_699_999_999UL, 1_700_000_000UL, 0u, true)]
    [InlineData(4_294_967_295UL + 1_700_000_000UL, 1_700_000_000UL, 0u, false)]
    [InlineData(4_294_967_296UL + 1_700_000_000UL, 1_700_000_000UL, 0u, true)]
    [InlineData(4_294_967_296UL + 1_700_000_000UL, 1_700_000_000UL, 500u, true)]
    public void NeedsExtendedInodeTime_KnownTimestamps_FollowCompactDeltaAndSharedNanosecondLimits(
        ulong timestamp, ulong epoch, uint nanoseconds, bool expectsExtended)
    {
        Assert.Equal(expectsExtended, ErofsBuilder.NeedsExtendedInodeTime(timestamp, epoch, nanoseconds));
    }

    [Fact]
    public void Build_ExtraDevicesWithFiveTwelveByteBlocks_KeepsSuperblockSlotIntact()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", "payload"u8.ToArray());
        var descriptors = new ErofsDeviceSpec[8];
        for (var index = 0; index < descriptors.Length; index++)
        {
            descriptors[index] = new ErofsDeviceSpec($"ext{index}.img", 4096);
        }

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = 512,
            Compress = false,
            ExtraDeviceCount = 8,
            ExtraDeviceImages = descriptors,
        });

        var superblock = ErofsSuperblock.FromImage(image);
        var tableStart = (long)superblock.DevtSlotoff * ErofsDeviceSlot.Size;
        Assert.Equal((ushort)8, superblock.ExtraDevices);
        Assert.True(tableStart >= ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize,
            $"The device table at byte {tableStart} overwrote the superblock slot.");
        for (var index = 0; index < descriptors.Length; index++)
        {
            var slot = ErofsDeviceSlot.FromBytes(
                image.AsSpan((int)(tableStart + index * ErofsDeviceSlot.Size), ErofsDeviceSlot.Size),
                superblock.Has48Bit);
            Assert.Equal($"ext{index}.img", Encoding.UTF8.GetString(slot.Tag).TrimEnd('\0'));
            Assert.Equal((ulong)(4096 / 512), slot.Blocks);
        }

        using var parser = new ErofsParser(image);
        Assert.Equal("payload", Encoding.UTF8.GetString(parser.ReadFileBytes("data.bin")));
    }
}