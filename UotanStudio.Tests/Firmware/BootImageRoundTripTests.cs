using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
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
    private static readonly byte[] TestVendorRamdisk = BuildBytes(4096, factor: 5);
    private static readonly byte[] TestVendorDtb = BuildBytes(2048, factor: 17);
    private static readonly byte[] TestVendorBootconfig = BuildBytes(512, factor: 23);

    /// <summary>
    /// Test helper: collects the streaming packer output into an in-memory byte array
    /// (replaces the obsolete <c>Pack()</c> convenience overload).
    /// <para>测试辅助方法：把流式打包输出收集为内存字节数组（替代已过时的 <c>Pack()</c>）。</para>
    /// </summary>
    private static byte[] PackToArray(BootImagePacker packer)
    {
        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
    }

    private static byte[] PackToArray(VendorBootPacker packer)
    {
        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
    }

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

        var image = PackToArray(packer);
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

        var image = PackToArray(packer);

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

        var image = PackToArray(packer);

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

    [Fact]
    public void VendorBoot_V3_RoundTrip_PreservesComponentsAndKeyFields()
    {
        var packer = new VendorBootPacker
        {
            Header = new VendorBootHeader
            {
                HeaderVersion = 3,
                PageSize = 4096,
                KernelLoadAddress = 0x00100000,
                RamdiskLoadAddress = 0x01000000,
                TagsLoadAddress = 0x00000100,
                VendorCmdline = "androidboot.vendor=test",
            },
            VendorRamdisk = TestVendorRamdisk,
            Dtb = TestVendorDtb,
        };

        var image = PackToArray(packer);
        Assert.StartsWith("VNDRBOOT", System.Text.Encoding.ASCII.GetString(image, 0, 8));

        using var parser = new VendorBootParser();
        parser.Parse(image);

        Assert.Equal(3u, parser.Header.HeaderVersion);
        Assert.Equal(packer.Header.PageSize, parser.Header.PageSize);
        Assert.Equal(TestVendorRamdisk, parser.VendorRamdisk);
        Assert.Equal(TestVendorDtb, parser.Dtb);
        Assert.Equal(packer.Header.KernelLoadAddress, parser.Header.KernelLoadAddress);
        Assert.Equal(packer.Header.RamdiskLoadAddress, parser.Header.RamdiskLoadAddress);
        Assert.Equal(packer.Header.TagsLoadAddress, parser.Header.TagsLoadAddress);
        Assert.Equal(packer.Header.VendorCmdline, parser.Header.VendorCmdline);
        Assert.Equal(2112u, parser.Header.VendorHeaderSize);
        Assert.Equal(0u, parser.Header.VendorBootconfigSize);
        Assert.Empty(parser.RamdiskTable);

        // Repack from the parser, then re-parse and verify components and key fields remain stable.
        var repacked = VendorBootPacker.Repack(parser);
        using var parser2 = new VendorBootParser();
        parser2.Parse(repacked);

        Assert.Equal(TestVendorRamdisk, parser2.VendorRamdisk);
        Assert.Equal(TestVendorDtb, parser2.Dtb);
        Assert.Equal(parser.Header.PageSize, parser2.Header.PageSize);
        Assert.Equal(parser.Header.VendorHeaderSize, parser2.Header.VendorHeaderSize);
        Assert.Equal(parser.Header.VendorCmdline, parser2.Header.VendorCmdline);
    }

    [Fact]
    public void VendorBoot_V4_RoundTrip_PreservesComponentsTableAndBootconfig()
    {
        var packer = new VendorBootPacker
        {
            Header = new VendorBootHeader
            {
                HeaderVersion = 4,
                PageSize = 4096,
                KernelLoadAddress = 0x00100000,
                RamdiskLoadAddress = 0x01000000,
                TagsLoadAddress = 0x00000100,
                VendorCmdline = "androidboot.vendor.slot_suffix=_a",
            },
            VendorRamdisk = TestVendorRamdisk,
            Dtb = TestVendorDtb,
            Bootconfig = TestVendorBootconfig,
        };
        packer.RamdiskTable.Add(new VendorRamdiskTableEntry
        {
            RamdiskOffset = 0,
            RamdiskSize = 1024,
            RamdiskType = 1,
            RamdiskName = "platform",
        });
        packer.RamdiskTable.Add(new VendorRamdiskTableEntry
        {
            RamdiskOffset = 1024,
            RamdiskSize = 1024,
            RamdiskType = 3,
            RamdiskName = "dlkm",
        });

        var image = PackToArray(packer);
        Assert.StartsWith("VNDRBOOT", System.Text.Encoding.ASCII.GetString(image, 0, 8));

        using var parser = new VendorBootParser();
        parser.Parse(image);

        Assert.Equal(4u, parser.Header.HeaderVersion);
        Assert.Equal(TestVendorRamdisk, parser.VendorRamdisk);
        Assert.Equal(TestVendorDtb, parser.Dtb);
        Assert.Equal(TestVendorBootconfig, parser.Bootconfig);
        Assert.Equal(packer.Header.VendorRamdiskTableSize, parser.Header.VendorRamdiskTableSize);
        Assert.Equal(packer.Header.VendorRamdiskTableEntryNum, parser.Header.VendorRamdiskTableEntryNum);
        Assert.Equal(packer.Header.VendorRamdiskTableEntrySize, parser.Header.VendorRamdiskTableEntrySize);
        Assert.Equal(packer.Header.VendorBootconfigSize, parser.Header.VendorBootconfigSize);
        Assert.Equal(packer.Header.VendorRamdiskTableOffset, parser.Header.VendorRamdiskTableOffset);

        Assert.Equal(2, parser.RamdiskTable.Count);
        Assert.Equal(0u, parser.RamdiskTable[0].RamdiskOffset);
        Assert.Equal(1024u, parser.RamdiskTable[0].RamdiskSize);
        Assert.Equal(1u, parser.RamdiskTable[0].RamdiskType);
        Assert.Equal("platform", parser.RamdiskTable[0].RamdiskName);
        Assert.Equal(1024u, parser.RamdiskTable[1].RamdiskOffset);
        Assert.Equal(1024u, parser.RamdiskTable[1].RamdiskSize);
        Assert.Equal(3u, parser.RamdiskTable[1].RamdiskType);
        Assert.Equal("dlkm", parser.RamdiskTable[1].RamdiskName);

        // Repack from the parser, then re-parse and verify components and the table remain stable.
        var repacked = VendorBootPacker.Repack(parser);
        using var parser2 = new VendorBootParser();
        parser2.Parse(repacked);

        Assert.Equal(TestVendorRamdisk, parser2.VendorRamdisk);
        Assert.Equal(TestVendorDtb, parser2.Dtb);
        Assert.Equal(TestVendorBootconfig, parser2.Bootconfig);
        Assert.Equal(2, parser2.RamdiskTable.Count);
        Assert.Equal(parser.RamdiskTable[0].RamdiskOffset, parser2.RamdiskTable[0].RamdiskOffset);
        Assert.Equal(parser.RamdiskTable[0].RamdiskSize, parser2.RamdiskTable[0].RamdiskSize);
        Assert.Equal(parser.RamdiskTable[0].RamdiskName, parser2.RamdiskTable[0].RamdiskName);
        Assert.Equal(parser.RamdiskTable[1].RamdiskName, parser2.RamdiskTable[1].RamdiskName);
    }

    [Fact]
    public void V0_UnpackRepack_PreservesComponentsAndKeyFields()
    {
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V0,
                PageSize = 2048,
                KernelAddr = 0x00008000,
                RamdiskAddr = 0x01000000,
                SecondAddr = 0x00F00000,
                TagsAddr = 0x00000100,
                OsVersion = 0x00010000,
                CommandLine = "console=ttyMSM0",
                ImageName = "v0-boot",
            },
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
            Second = BuildBytes(256, factor: 29),
        };

        var image = PackToArray(packer);
        Assert.StartsWith("ANDROID!", System.Text.Encoding.ASCII.GetString(image, 0, 8));

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(BootImageVersion.V0, parser.Header.HeaderVersion);
        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(packer.Second, parser.Second);
        Assert.Equal([], parser.Dtb);
        Assert.Equal([], parser.RecoveryDtbo);
        Assert.Equal(packer.Header.CommandLine, parser.Header.CommandLine);

        var repacked = BootImagePacker.Repack(parser);
        using var parser2 = new BootImageParser();
        parser2.Parse(repacked);

        Assert.Equal(BootImageVersion.V0, parser2.Header.HeaderVersion);
        Assert.Equal(TestKernel, parser2.Kernel);
        Assert.Equal(TestRamdisk, parser2.Ramdisk);
        Assert.Equal(packer.Second, parser2.Second);
    }

    [Fact]
    public void V1_UnpackRepack_WithRecoveryDtbo_PreservesComponents()
    {
        var recoveryDtbo = BuildBytes(1024, factor: 31);
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V1,
                PageSize = 4096,
                KernelAddr = 0x00008000,
                RamdiskAddr = 0x01000000,
                TagsAddr = 0x00000100,
                CommandLine = "console=ttyS0",
            },
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
            RecoveryDtbo = recoveryDtbo,
        };

        var image = PackToArray(packer);

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(BootImageVersion.V1, parser.Header.HeaderVersion);
        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(recoveryDtbo, parser.RecoveryDtbo);
        Assert.True(parser.Header.RecoveryDtboOffset > 0);
        Assert.Equal(recoveryDtbo.Length, (int)parser.Header.RecoveryDtboSize);

        var repacked = BootImagePacker.Repack(parser);
        using var parser2 = new BootImageParser();
        parser2.Parse(repacked);

        Assert.Equal(recoveryDtbo, parser2.RecoveryDtbo);
        Assert.Equal(parser.Header.RecoveryDtboOffset, parser2.Header.RecoveryDtboOffset);
    }

    [Fact]
    public void V2_UnpackRepack_WithRecoveryDtboAndDtb_PreservesBoth()
    {
        // mkbootimg 布局回归：recovery_dtbo 位于 second 之后，dtb 在 recovery_dtbo 之后。
        // mkbootimg layout regression: recovery_dtbo follows second, dtb follows recovery_dtbo.
        var recoveryDtbo = BuildBytes(1024, factor: 37);
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V2,
                PageSize = 4096,
                KernelAddr = 0x00008000,
                RamdiskAddr = 0x01000000,
                TagsAddr = 0x00000100,
                KernelLoadAddr = 0x01F00000,
                CommandLine = "console=ttyS0",
            },
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
            RecoveryDtbo = recoveryDtbo,
            Dtb = TestDtb,
        };

        var image = PackToArray(packer);

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(BootImageVersion.V2, parser.Header.HeaderVersion);
        Assert.Equal(recoveryDtbo, parser.RecoveryDtbo);
        Assert.Equal(TestDtb, parser.Dtb);

        var repacked = BootImagePacker.Repack(parser);
        using var parser2 = new BootImageParser();
        parser2.Parse(repacked);

        Assert.Equal(recoveryDtbo, parser2.RecoveryDtbo);
        Assert.Equal(TestDtb, parser2.Dtb);
    }

    [Fact]
    public void V0_TruncatedImage_ParseFails()
    {
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V0,
                PageSize = 2048,
            },
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        };

        var image = PackToArray(packer);
        // 整页截断：解析器对超出镜像末尾的段按既有约定返回空组件（宽松截断语义）。
        // A full page is removed; per the existing contract the parser yields empty
        // components for segments extending past EOF (lenient truncation semantics).
        var truncated = new byte[image.Length - 2048];
        Array.Copy(image, truncated, truncated.Length);

        using var parser = new BootImageParser();
        parser.Parse(truncated);

        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal([], parser.Ramdisk);
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