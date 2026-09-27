using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guard tests for the batch-1.4 section-wrapper investigation (BGPT / PLMZ / TICLE). Upstream Magisk was fetched
/// and inspected (native/src/boot/bootimg.cpp, compress.rs, format.rs, patch.rs, lib.rs at master and tags v26.0,
/// v27.0, v28.0, v29.0; the only FileFormat wrappers there are gzip/xz/lzma/bzip2/lz4/lz4_legacy/lz4_lg plus
/// ACCLAIM, SEANDROID and LG_BUMP tails), and osm0sis mboot and the Android Image Kitchen contain none of the
/// BGPT, PLMZ, TICLE or LQEC strings, so no byte layout could be proven and no unwrapping is implemented. These
/// tests pin the shipped contract instead: boot component bytes with such leading magics stay verbatim through
/// detection, decompression and unpack → repack, which is exactly what a future wrapper decoder must preserve.
/// <para>批次 1.4 区段封装调查（BGPT / PLMZ / TICLE）的守门测试。已抓取并检查上游 Magisk
/// （native/src/boot 的 bootimg.cpp、compress.rs、format.rs、patch.rs、lib.rs，master 及 v26.0、v27.0、v28.0、v29.0 标签；
/// 其中仅有的封装格式为 gzip/xz/lzma/bzip2/lz4/lz4_legacy/lz4_lg，外加 ACCLAIM、SEANDROID、LG_BUMP 尾部），
/// osm0sis mboot 与 Android Image Kitchen 亦无 BGPT、PLMZ、TICLE、LQEC 字符串，因此无法证实任何字节布局，未实现解封装。
/// 本文件锁定实际交付的契约：以这些魔数开头的 boot 组件字节在检测、解压与解包 → 重打包全程逐字节保持不变，
/// 这正是未来封装解码器必须维持的性质。</para>
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