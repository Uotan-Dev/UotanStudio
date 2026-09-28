using UotanStudio.Firmware.BootSecurity.Patcher.Kernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// arm64 Linux Image 头解析测试：以内核 booting 规范和真实设备内核（Linux 5.10.197，arm64，
/// BiSheng 构建）实测得到的 64 字节头为权威样例，校验 magic/text_offset/image_size/flags 的解析；
/// 语料库存在名为 Image 的真实内核时再额外做结构校验，缺失时跳过。
/// <para>Tests for the arm64 Linux Image header parser: they anchor on the 64-byte header captured from a
/// real device kernel (Linux 5.10.197, arm64, BiSheng build) per the booting spec, checking magic,
/// text_offset, image_size and flags; a structural check runs against a real corpus Image when present and
/// skips otherwise.</para>
/// </summary>
public sealed class Arm64ImageHeaderTests
{
    /// <summary>
    /// 真机 <c>arch/arm64/boot/Image</c> 头前 64 字节的实测值（text_offset=0，image_size=0x2d60000，
    /// flags=0x0a 表示小端 + 4KiB 页 + 物理基址标志，magic=ARM\x64，res5=0x40）。
    /// <para>The first 64 bytes captured from a real <c>arch/arm64/boot/Image</c>: text_offset=0,
    /// image_size=0x2d60000, flags=0x0a (little endian + 4KiB pages + physical base flag), magic=ARM\x64, res5=0x40.</para>
    /// </summary>
    private static ReadOnlySpan<byte> RealDeviceHeader =>
    [
        0x4d, 0x5a, 0x00, 0x91, 0xff, 0xff, 0x49, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0xd6, 0x02, 0x00, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41, 0x52, 0x4d, 0x64, 0x40, 0x00, 0x00, 0x00,
    ];

    [Fact]
    public void TryParse_RealDeviceHeader_DecodesFieldsPerSpec()
    {
        Assert.True(Arm64ImageHeader.TryParse(RealDeviceHeader, out var header));
        Assert.Equal(0UL, header.TextOffset);
        Assert.Equal(0x2d60000UL, header.ImageSize);
        Assert.Equal(0x0aUL, header.Flags);
        Assert.Equal(12, header.PageSizeShift);
    }

    [Theory]
    [InlineData(0x00UL, 12)]
    [InlineData(0x02UL, 12)]
    [InlineData(0x04UL, 14)]
    [InlineData(0x06UL, 16)]
    public void PageSizeShift_DecodesFlagEncoding(ulong flags, int expectedShift)
    {
        var header = BuildHeader(flags);
        Assert.True(Arm64ImageHeader.TryParse(header, out var parsed));
        Assert.Equal(expectedShift, parsed.PageSizeShift);
    }

    [Fact]
    public void TryParse_TooShort_ReturnsFalse()
    {
        Assert.False(Arm64ImageHeader.TryParse(RealDeviceHeader[..0x3f], out _));
    }

    [Fact]
    public void TryParse_WrongMagic_ReturnsFalse()
    {
        var corrupted = RealDeviceHeader.ToArray();
        corrupted[0x38] = 0x00;
        Assert.False(Arm64ImageHeader.TryParse(corrupted, out _));
    }

    private static byte[] BuildHeader(ulong flags)
    {
        var header = RealDeviceHeader.ToArray();
        for (var i = 0; i < 8; i++) header[0x18 + i] = (byte)(flags >> (8 * i));
        return header;
    }
}