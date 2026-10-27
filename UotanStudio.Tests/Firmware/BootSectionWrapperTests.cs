using System.Text;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guard tests for the section-wrapper magics BGPT / PLMZ / TICLE: no byte layout is defined for them, so no
/// unwrapping exists. These tests pin the shipped contract: boot component bytes with such leading magics stay
/// verbatim through detection, decompression and unpack → repack.
/// <para>区段封装魔数 BGPT / PLMZ / TICLE 的守门测试：这些魔数没有已定义的字节布局，因此不做解封装。
/// 本文件锁定实际交付的契约：以这些魔数开头的 boot 组件字节在检测、解压与解包 → 重打包全程逐字节保持不变。</para>
/// </summary>
public class BootSectionWrapperTests
{
    private static readonly byte[] TestKernel = BuildBytes(1024, 7);

    private static byte[] BuildBytes(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor + 3) & 0xFF);
        }

        return data;
    }

    private static byte[] Wrapped(string magic, int payloadLength = 64)
    {
        var data = new byte[magic.Length + payloadLength];
        Encoding.ASCII.GetBytes(magic).CopyTo(data, 0);
        BuildBytes(payloadLength, 5).CopyTo(data.AsSpan(magic.Length));
        return data;
    }

    private static byte[] PackToArray(BootImagePacker packer)
    {
        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("BGPT")]
    [InlineData("PLMZ")]
    [InlineData("TICLE")]
    [InlineData("LQEC")]
    public void Detect_SyntheticWrapperMagic_IsReportedAsRawBytes(string magic)
    {
        Assert.Equal(CompressionType.None, BootCompressor.Detect(Wrapped(magic)));
    }

    [Theory]
    [InlineData("BGPT")]
    [InlineData("PLMZ")]
    [InlineData("TICLE")]
    [InlineData("LQEC")]
    public void DecompressOrReturn_SyntheticWrapperPayload_ReturnsBytesVerbatim(string magic)
    {
        var data = Wrapped(magic);

        var result = BootCompressor.DecompressOrReturn(data);

        Assert.Equal(data, result);
    }

    [Fact]
    public void BootV4_WrappedRamdisk_RoundTripsByteIdenticallyWithWrappedBytesPreserved()
    {
        var wrapped = Wrapped("BGPT");
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V4,
                HeaderSize = 1584,
                PageSize = 4096,
                OsVersion = 0x01040000,
                CommandLine = "androidboot.ramdisk_wrap=BGPT",
            },
            Kernel = TestKernel,
            Ramdisk = wrapped,
        };
        var image = PackToArray(packer);

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(wrapped, parser.Ramdisk);
        Assert.Equal(CompressionType.None, parser.Header.RamdiskCompression);
        Assert.Equal(image, BootImagePacker.Repack(parser));
    }

    [Fact]
    public void VendorBootV4_WrappedVendorRamdisk_ExtractionYieldsWrappedBytesUnchanged()
    {
        var wrapped = Wrapped("PLMZ", payloadLength: 512);
        var packer = new VendorBootPacker
        {
            Header = new VendorBootHeader
            {
                HeaderVersion = 4,
                PageSize = 4096,
                VendorCmdline = "androidboot.vendor=wrap",
            },
            VendorRamdisk = wrapped,
            Dtb = BuildBytes(64, 11),
        };
        using var vendorMs = new MemoryStream();
        packer.PackToStream(vendorMs);
        var image = vendorMs.ToArray();

        using var parser = new VendorBootParser();
        parser.Parse(image);

        Assert.Equal(wrapped, parser.VendorRamdisk);
        Assert.Equal(CompressionType.None, parser.Header.VendorRamdiskCompression);
        using var stream = parser.ExtractComponentStream(VendorBootComponent.VendorRamdisk);
        using var copied = new MemoryStream();
        stream.CopyTo(copied);
        Assert.Equal(wrapped, copied.ToArray());
    }
}