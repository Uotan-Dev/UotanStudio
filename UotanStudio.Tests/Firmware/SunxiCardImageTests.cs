using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class SunxiCardImageTests
{
    private const int BlockSize = 512;
    private const int EgonOffset = 0x2000;
    private const int EgonLength = 0x10000;
    private const int FirstUsableLba = 34816;
    private const int BootPackageBlock = 32800;
    private const int PartitionCount = 128;
    private const int PartitionEntrySize = 128;
    private const string CorpusVariable = "UOTAN_SUNXI_CARD_CORPUS";

    private static readonly (string Name, int FirstLba, int Sectors)[] Layout =
    [
        ("boot-resource", 34816, 48),
        ("env", 34864, 8 + 504),
        ("env-redund", 34872 + 504, 8 + 504),
        ("boot", 34880, 64),
        ("rootfs", 34944, 96),
        ("dsp0", 35040, 16),
        ("recovery", 35056, 16)
    ];

    public SunxiCardImageTests() => FirmwareFormatRegistry.RegisterAll();

    [Fact]
    public void MatchesMagic_AllThreeMarkers_RoutesToCardImageHandler()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("card.img", BuildImage());

        var detected = FormatDetector.DetectDetailed(path);

        Assert.NotNull(detected);
        Assert.Equal("Allwinner SD Card Image", detected!.Handler.FormatName);
    }

    [Fact]
    public void Detect_GptHeaderWithSameLayoutButNoEgon_StaysWithGenericGptHandler()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("plain.img", BuildImage(withEgon: false));

        var detected = FormatDetector.DetectDetailed(path);

        Assert.NotNull(detected);
        Assert.Equal("GUID Partition Table (GPT)", detected!.Handler.FormatName);
    }

    [Fact]
    public async Task ReadMetadataAsync_BuiltImage_ReportsPartitionsBootRegionAndEnvironments()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("card.img", BuildImage());

        var result = await FormatDetector.Detect(path)!.ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(FirstUsableLba.ToString(), result.Metadata["FirstUsableLba"]);
        Assert.Equal(Layout.Length.ToString(), result.Metadata["PartitionCount"]);
        Assert.Equal("0x2000", result.Metadata["Boot0Offset"]);
        Assert.Equal("0x20000", result.Metadata["Boot0DeviceTreeWord"]);
        Assert.Equal("4.0", result.Metadata["Boot0VersionText"]);
        Assert.Equal("0x" + (BootPackageBlock * BlockSize).ToString("X"), result.Metadata["BootPackageOffset"]);
        Assert.Equal("3", result.Metadata["BootPackageItems"]);
        Assert.Contains("BootPackageItem:opensbi", result.Metadata.Keys);
        Assert.Contains(result.Entries, line => line.StartsWith("boot-package:opensbi ", StringComparison.Ordinal));
        Assert.Equal("true", result.Metadata["Env:env:CrcMatches"]);
        Assert.Equal("run distro_bootcmd", result.Metadata["Env:env:bootcmd"]);
        Assert.Contains("BootResource:BOOTLOGO.BMP", result.Metadata.Keys);
        Assert.Contains(result.Entries, line => line.StartsWith("rootfs ", StringComparison.Ordinal)
            && line.Contains("SquashFs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnpackAsync_BuiltImage_CarvesTrimmedFexPerPartition()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("card.img", BuildImage());
        var output = Path.Combine(temp.Path, "out");

        var result = await FormatDetector.Detect(path)!
            .UnpackAsync(path, output, new UnpackOptions(), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(Layout.Length, result.Components.Count);
        var names = result.Components.Select(component => component.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["boot-resource.fex", "boot.fex", "dsp0.fex", "env-redund.fex", "env.fex", "recovery.fex",
            "rootfs.fex"], names);
        var rootfs = await File.ReadAllBytesAsync(Path.Combine(output, "rootfs.fex"),
            TestContext.Current.CancellationToken);
        Assert.True(rootfs.Length < 96 * BlockSize, $"expected the trailing fill to be trimmed, got {rootfs.Length}");
        Assert.True(rootfs.AsSpan().StartsWith("hsqs"u8));
        var env = await File.ReadAllBytesAsync(Path.Combine(output, "env.fex"), TestContext.Current.CancellationToken);
        Assert.Equal(SunxiEnvironmentBlock.StorageLength, env.Length);
    }

    [Fact]
    public async Task UnpackAsync_SelectedEntries_ExtractsOnlyTheRequestedPartition()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("card.img", BuildImage());
        var output = Path.Combine(temp.Path, "out");

        var result = await FormatDetector.Detect(path)!.UnpackAsync(path, output,
            new UnpackOptions { SelectedEntries = ["env"] }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(["env.fex"], result.Components.Select(component => component.Name));
        Assert.False(File.Exists(Path.Combine(output, "rootfs.fex")));
    }

    [Fact]
    public async Task UnpackAsync_MetadataOnly_IsRejectedInsteadOfSilentlyExtracting()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("card.img", BuildImage());
        var output = Path.Combine(temp.Path, "out");

        var result = await FormatDetector.Detect(path)!.UnpackAsync(path, output,
            new UnpackOptions { MetadataOnly = true }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("metadata-only", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any());
    }

    [Fact]
    public async Task ReadMetadataAsync_RealVendorCorpus_CarvesEveryVendorFexByteForByte()
    {
        var image = RequireCorpusImage();
        await using var stream = File.OpenRead(image);
        var parsed = SunxiCardImageFormat.Read(stream, TestContext.Current.CancellationToken);
        var vendor = Path.Combine(Path.GetDirectoryName(image)!, "vendor-dump");
        Assert.SkipUnless(Directory.Exists(vendor),
            $"Place the vendor .fex dump of the corpus image in '{vendor}' to run the byte-level comparison.");

        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var result = await FormatDetector.Detect(image)!
            .UnpackAsync(image, output, new UnpackOptions(), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(parsed.Partitions.Count, result.Components.Count);
        Assert.NotEmpty(parsed.BootResourceFiles);
        Assert.All(parsed.Environments.Values, environment => Assert.True(environment.CrcMatches));
        foreach (var partition in parsed.Partitions)
        {
            var counterpart = Path.Combine(vendor, partition.ExportName);
            if (!File.Exists(counterpart))
            {
                continue;
            }

            var expected = await File.ReadAllBytesAsync(counterpart, TestContext.Current.CancellationToken);
            var actual = await File.ReadAllBytesAsync(Path.Combine(output, partition.ExportName),
                TestContext.Current.CancellationToken);

            var shared = Math.Min(actual.Length, expected.Length);
            Assert.True(actual.AsSpan(0, shared).SequenceEqual(expected.AsSpan(0, shared)),
                $"{partition.ExportName}: carved bytes differ from the vendor dump in the first {shared} B");
            var longer = actual.Length >= expected.Length ? actual.AsSpan() : expected.AsSpan();
            Assert.True(longer[shared..].IndexOfAnyExcept((byte)0) < 0,
                $"{partition.ExportName}: the length difference between {actual.Length} B and {expected.Length} B is not NUL padding");
        }
    }

    private static string RequireCorpusImage()
    {
        var directory = Environment.GetEnvironmentVariable(CorpusVariable);
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
            $"Set {CorpusVariable} to a directory holding an Allwinner SD-card image to run the vendor corpus check.");
        var matches = Directory.EnumerateFiles(directory!, "*.img", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.SkipUnless(matches.Length > 0,
            $"Set {CorpusVariable} to a directory containing an Allwinner SD-card image to run this acceptance.");
        return matches[0];
    }
    private static byte[] BuildImage(bool withEgon = true)
    {
        var partitions = Layout.Select(entry => new SunxiCardImageBuilder.Partition(
            entry.Name, entry.Sectors, BuildPartitionContent(entry.Name))).ToArray();
        var image = SunxiCardImageBuilder.Build(partitions);
        if (!withEgon)
        {
            image.AsSpan(SunxiCardImageBuilder.EgonOffset, 12).Clear();
        }

        return image;
    }

    private static byte[] BuildPartitionContent(string name)
    {
        var content = new byte[0];
        switch (name)
        {
            case "boot-resource":
                content = new byte[1024 + (16 * 32) + 512];
                WriteFatVolume(content);
                break;
            case "env":
            case "env-redund":
                content = new byte[SunxiEnvironmentBlock.StorageLength];
                WriteEnvironment(content);
                break;
            case "rootfs":
                content = new byte[512];
                "hsqs"u8.CopyTo(content);
                break;
            case "boot":
                content = new byte[4096];
                "ANDROID!"u8.CopyTo(content);
                BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(8), 0x400);
                BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(12), 0x40200000);
                BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(44), 2048);
                Encoding.ASCII.GetBytes("d1-nezha").CopyTo(content.AsSpan(48));
                "RISCV"u8.CopyTo(content.AsSpan(2048 + 56));
                break;
            case "dsp0":
                content = new byte[512];
                content[0] = 0x7F;
                content[1] = (byte)'E';
                content[2] = (byte)'L';
                content[3] = (byte)'F';
                content[4] = 1;
                content[5] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(18), 0xF3);
                break;
            default:
                content = "blank.fex\n"u8.ToArray();
                break;
        }

        return content;
    }

    private static void WriteEnvironment(Span<byte> slot)
    {
        var area = SunxiEnvironmentBlock.StorageLength;
        var body = slot.Length >= area ? slot[..area] : slot;
        var text = "bootcmd=run distro_bootcmd\0bootdelay=0\0mmc_root=/dev/mmcblk0p5\0"u8;
        text.CopyTo(body[5..]);
        BinaryPrimitives.WriteUInt32LittleEndian(body, Crc32Ieee.Compute(body[5..]));
        body[4] = 0x01;
    }

    private static void WriteFatVolume(Span<byte> slot)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(slot[11..], 512);
        slot[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(slot[14..], 1);
        slot[16] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(slot[17..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(slot[22..], 1);
        "FAT16   "u8.CopyTo(slot[54..]);

        var rootOffset = (1 + (1 * 1)) * 512;
        var entry = slot.Slice(rootOffset, 32);
        Encoding.ASCII.GetBytes("BOOTLOGOBMP").CopyTo(entry);
        entry[11] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], 70);

        var data = slot.Slice(rootOffset + (16 * 32), 4);
        data[0] = (byte)'B';
        data[1] = (byte)'M';
    }
}