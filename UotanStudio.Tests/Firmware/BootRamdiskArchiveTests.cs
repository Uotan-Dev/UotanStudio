using System.Text;
using UotanStudio.Compress;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Services.Firmware;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Covers the CPIO component that the application boot and vendor boot unpack paths publish next to the raw
/// ramdisk: the component must only appear when the decoded ramdisk really is an archive, and a compressed
/// ramdisk must still decode into that archive.
/// <para>覆盖 boot 与 vendor_boot 解包在原始 ramdisk 旁发布的 CPIO 组件：只有解码结果确实是归档时才应出现该
/// 组件，而压缩的 ramdisk 仍必须解码出该归档。</para>
/// </summary>
public sealed class BootRamdiskArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-ramdisk-archive-" + Guid.NewGuid().ToString("N"));

    public BootRamdiskArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task VendorBoot_Lz4LegacyRamdisk_WritesDecodedCpioComponent()
    {
        var token = TestContext.Current.CancellationToken;
        var cpio = BuildCpio();
        var compressed = CompressionService.Compress(cpio, CompressionFormat.Lz4Legacy);
        var image = Path.Combine(_root, "vendor_boot.img");
        new VendorBootPacker
        {
            Header = new VendorBootHeader
            {
                HeaderVersion = 4,
                PageSize = 4096,
                VendorName = "test-device",
                VendorCmdline = "vendor.test=1"
            },
            VendorRamdisk = compressed,
            Dtb = [5, 6],
            Bootconfig = [7, 8]
        }.PackToFile(image);

        var output = Path.Combine(_root, "vendor_boot");
        var result = await FirmwareService.Instance.UnpackBootAsync(image, output, token,
            RepackMetadataContext.LocalDirectory);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(compressed, await File.ReadAllBytesAsync(Path.Combine(output, "vendor_ramdisk"), token));
        Assert.Equal(cpio, await File.ReadAllBytesAsync(Path.Combine(output, "vendor_ramdisk.cpio"), token));
    }

    [Fact]
    public async Task VendorBoot_UndecodableRamdisk_SkipsCpioComponent()
    {
        var token = TestContext.Current.CancellationToken;
        var image = Path.Combine(_root, "vendor_boot.img");
        byte[] ramdisk = [0x1F, 0x8B, 0x08, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77];
        new VendorBootPacker
        {
            Header = new VendorBootHeader
            {
                HeaderVersion = 4,
                PageSize = 4096,
                VendorName = "test-device"
            },
            VendorRamdisk = ramdisk,
            Dtb = [5, 6]
        }.PackToFile(image);

        var output = Path.Combine(_root, "vendor_boot");
        var result = await FirmwareService.Instance.UnpackBootAsync(image, output, token,
            RepackMetadataContext.LocalDirectory);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(ramdisk, await File.ReadAllBytesAsync(Path.Combine(output, "vendor_ramdisk"), token));
        Assert.False(File.Exists(Path.Combine(output, "vendor_ramdisk.cpio")));
    }

    [Fact]
    public async Task Boot_PlainNonArchiveRamdisk_SkipsCpioComponent()
    {
        var token = TestContext.Current.CancellationToken;
        var image = Path.Combine(_root, "boot.img");
        new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V4,
                PageSize = 4096,
                HeaderSize = 1584,
                CommandLine = "console=ttyS0",
                KernelAddr = 0x8000,
                RamdiskAddr = 0x100000
            },
            Kernel = [1, 2, 3],
            Ramdisk = [4, 5, 6]
        }.PackToFile(image);

        var output = Path.Combine(_root, "boot");
        var result = await FirmwareService.Instance.UnpackBootAsync(image, output, token,
            RepackMetadataContext.LocalDirectory);

        Assert.True(result.Success, result.Summary);
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(Path.Combine(output, "ramdisk"), token));
        Assert.False(File.Exists(Path.Combine(output, "ramdisk.cpio")));
    }

    [Fact]
    public async Task Boot_GzipRamdisk_WritesDecodedCpioComponent()
    {
        var token = TestContext.Current.CancellationToken;
        var cpio = BuildCpio();
        var compressed = CompressionService.Compress(cpio, CompressionFormat.Gzip);
        var image = Path.Combine(_root, "boot.img");
        new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V4,
                PageSize = 4096,
                HeaderSize = 1584,
                KernelAddr = 0x8000,
                RamdiskAddr = 0x100000
            },
            Kernel = [1, 2, 3],
            Ramdisk = compressed
        }.PackToFile(image);

        var output = Path.Combine(_root, "boot");
        var result = await FirmwareService.Instance.UnpackBootAsync(image, output, token,
            RepackMetadataContext.LocalDirectory);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(compressed, await File.ReadAllBytesAsync(Path.Combine(output, "ramdisk"), token));
        Assert.Equal(cpio, await File.ReadAllBytesAsync(Path.Combine(output, "ramdisk.cpio"), token));
    }

    private static byte[] BuildCpio()
    {
        using var stream = new MemoryStream();
        new CpioArchive().SaveAll(stream,
        [
            new ArchiveEntry
            {
                Name = "init",
                Data = Encoding.UTF8.GetBytes("ramdisk"),
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    Mode = 0x81A4,
                    UserId = 0,
                    GroupId = 0,
                    LinkCount = 1,
                    UnixPermissions = 0x1A4,
                    ModificationTimeUnixSeconds = 1_700_000_000
                }
            }
        ], ArchiveFormat.NewAscii);
        return stream.ToArray();
    }
}