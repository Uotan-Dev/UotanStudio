using UotanStudio.Firmware.BootImage;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Boot image unpack → repack round-trip tests for header versions v2 / v3 / v4,
/// constructed entirely in memory (kernel + ramdisk + dtb components).
/// <para>头版本 v2 / v3 / v4 的 boot 镜像解包 → 重打包往返测试，完全在内存中构造
/// （kernel + ramdisk + dtb 组件）。</para>
/// </summary>
public class BootImageRoundTripTests
{
    private static readonly byte[] TestKernel = BuildBytes(2048, factor: 7);
    private static readonly byte[] TestRamdisk = BuildBytes(1024, factor: 3);
    private static readonly byte[] TestDtb = BuildBytes(512, factor: 11);
    private static readonly byte[] TestSignature = BuildBytes(256, factor: 13);

    [Fact]
    public void V2_UnpackRepack_PreservesComponentsAndKeyFields()
    {
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V2,
                PageSize = 2048,
                KernelAddr = 0x00008000,
                RamdiskAddr = 0x01000000,
                SecondAddr = 0x00F00000,
                TagsAddr = 0x00000100,
                KernelLoadAddr = 0x01F00000,
                OsVersion = 0x00010002,
                CommandLine = "console=ttyS0 androidboot.hardware=test",
                ImageName = "test-boot",
            },
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
            Dtb = TestDtb,
        };

        var image = packer.Pack();
        Assert.StartsWith("ANDROID!", System.Text.Encoding.ASCII.GetString(image, 0, 8));

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(BootImageVersion.V2, parser.Header.HeaderVersion);
        Assert.Equal(packer.Header.PageSize, parser.Header.PageSize);
        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(TestDtb, parser.Dtb);
        Assert.Equal(packer.Header.KernelAddr, parser.Header.KernelAddr);
        Assert.Equal(packer.Header.RamdiskAddr, parser.Header.RamdiskAddr);
        Assert.Equal(packer.Header.OsVersion, parser.Header.OsVersion);
        Assert.Equal(packer.Header.CommandLine, parser.Header.CommandLine);

        // Repack from the parser, then re-parse and verify key fields remain stable.
        var repacked = BootImagePacker.Repack(parser);
        using var parser2 = new BootImageParser();
        parser2.Parse(repacked);

        Assert.Equal(parser.Header.HeaderVersion, parser2.Header.HeaderVersion);
        Assert.Equal(TestKernel, parser2.Kernel);
        Assert.Equal(TestRamdisk, parser2.Ramdisk);
        Assert.Equal(TestDtb, parser2.Dtb);
        Assert.Equal(parser.Header.PageSize, parser2.Header.PageSize);
        Assert.Equal(parser.Header.KernelSize, parser2.Header.KernelSize);
        Assert.Equal(parser.Header.RamdiskSize, parser2.Header.RamdiskSize);
        Assert.Equal(parser.Header.DtbSize, parser2.Header.DtbSize);
        Assert.Equal(parser.Header.OsVersion, parser2.Header.OsVersion);
        Assert.Equal(parser.Header.CommandLine, parser2.Header.CommandLine);
    }

    [Fact]
    public void V3_UnpackRepack_PreservesComponentsAndKeyFields()
    {
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V3,
                HeaderSize = 1580,
                OsVersion = 0x01030000,
                CommandLine = "androidboot.slot_suffix=_a",
            },
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        };

        var image = packer.Pack();

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(BootImageVersion.V3, parser.Header.HeaderVersion);
        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(4096u, parser.Header.PageSize);
        Assert.Equal(packer.Header.OsVersion, parser.Header.OsVersion);
        Assert.Equal(packer.Header.CommandLine, parser.Header.CommandLine);

        var repacked = BootImagePacker.Repack(parser);
        using var parser2 = new BootImageParser();
        parser2.Parse(repacked);

        Assert.Equal(parser.Header.HeaderVersion, parser2.Header.HeaderVersion);
        Assert.Equal(TestKernel, parser2.Kernel);
        Assert.Equal(TestRamdisk, parser2.Ramdisk);
        Assert.Equal(parser.Header.OsVersion, parser2.Header.OsVersion);
        Assert.Equal(parser.Header.CommandLine, parser2.Header.CommandLine);
    }

    [Fact]
    public void V4_UnpackRepack_PreservesComponentsAndSignature()
    {
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V4,
                HeaderSize = 1584,
                OsVersion = 0x01040000,
                CommandLine = "androidboot.verifiedbootstate=green",
                SignatureSize = (uint)TestSignature.Length,
            },
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
            Signature = TestSignature,
        };

        var image = packer.Pack();

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(BootImageVersion.V4, parser.Header.HeaderVersion);
        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(TestSignature, parser.Signature);

        var repacked = BootImagePacker.Repack(parser);
        using var parser2 = new BootImageParser();
        parser2.Parse(repacked);

        Assert.Equal(parser.Header.HeaderVersion, parser2.Header.HeaderVersion);
        Assert.Equal(TestKernel, parser2.Kernel);
        Assert.Equal(TestRamdisk, parser2.Ramdisk);
        Assert.Equal(TestSignature, parser2.Signature);
    }

    private static byte[] BuildBytes(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor) & 0xFF);
        }

        return data;
    }
}
