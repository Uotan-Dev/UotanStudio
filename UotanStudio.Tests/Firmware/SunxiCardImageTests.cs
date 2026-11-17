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
        ("env", 34864, 8),
        ("env-redund", 34872, 8),
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
        Assert.Equal(Math.Min(8 * BlockSize, SunxiEnvironmentBlock.StorageLength), env.Length);
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
        const int gptBlock = 2048;
        const int usableStart = FirstUsableLba;
        var totalBlocks = usableStart + Layout.Sum(entry => entry.Sectors) + 64;
        var image = new byte[totalBlocks * BlockSize];

        image[510] = 0x55;
        image[511] = 0xAA;
        image[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(446 + 8), 1);

        if (withEgon)
        {
            WriteEgon(image.AsSpan(EgonOffset, EgonLength));
            WriteBootPackage(image.AsSpan(BootPackageBlock * BlockSize));
        }

        WriteGpt(image, totalBlocks, gptBlock, usableStart);
        foreach (var (name, first, sectors) in Layout)
        {
            WritePartition(image.AsSpan(first * BlockSize, sectors * BlockSize), name);
        }

        return image;
    }

    private static void WriteEgon(Span<byte> slot)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(slot, 0x3C80006F);
        "eGON.BT0"u8.CopyTo(slot[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], 0x191DABB2);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[16..], 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[20..], 0x00000030);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[32..], 0x20000);
        Encoding.ASCII.GetBytes("4.0\0").CopyTo(slot[48..]);
    }

    private static void WriteBootPackage(Span<byte> package)
    {
        "sunxi-package"u8.CopyTo(package);
        BinaryPrimitives.WriteUInt32LittleEndian(package[16..], 0x89119800);
        BinaryPrimitives.WriteUInt32LittleEndian(package[20..], 0xA78ACD0B);
        BinaryPrimitives.WriteUInt32LittleEndian(package[32..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(package[36..], 0xFC000);

        WritePackageItem(package, 0, "opensbi", 0x800, 0x400, 0x0, "OpenSBI v0.9"u8);
        WritePackageItem(package, 1, "u-boot", 0xC00, 0x800, 0x42000000, "U-Boot 2018.05"u8);
        WritePackageItem(package, 2, "dtb", 0x1400, 0x100, 0x0, (ReadOnlySpan<byte>)[0xD0, 0x0D, 0xFE, 0xED]);
    }

    private static void WritePackageItem(Span<byte> package, int index, string name, uint offset, uint length,
        uint runningAddress, ReadOnlySpan<byte> payload)
    {
        var record = package.Slice(0x3C + (index * 0x170), 0x170);
        "MIE;"u8.CopyTo(record);
        Encoding.ASCII.GetBytes(name).CopyTo(record[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x44..], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x48..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x50..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x54..], runningAddress);
        payload.CopyTo(package[(int)offset..]);
    }

    private static void WriteGpt(Span<byte> image, int totalBlocks, int gptBlock, int usableStart)
    {
        var entries = image.Slice(gptBlock * BlockSize, PartitionCount * PartitionEntrySize);
        for (var index = 0; index < Layout.Length; index++)
        {
            var (name, first, sectors) = Layout[index];
            var entry = entries.Slice(index * PartitionEntrySize, PartitionEntrySize);
            new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4").TryWriteBytes(entry, bigEndian: false, out _);
            new Guid(0x1000 + index, 0x2000, 0x3000, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0)
                .TryWriteBytes(entry[16..], bigEndian: false, out _);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], (ulong)first);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], (ulong)(first + sectors - 1));
            Encoding.Unicode.GetBytes(name).CopyTo(entry[56..]);
        }

        ulong lastUsable = (ulong)totalBlocks - 34;
        var entriesCrc = Crc32Ieee.Compute(entries);
        WriteHeader(image.Slice(BlockSize, BlockSize), 1, (ulong)totalBlocks - 1, (ulong)usableStart, lastUsable,
            (ulong)gptBlock, entriesCrc);
        WriteHeader(image.Slice((totalBlocks - 1) * BlockSize, BlockSize), (ulong)totalBlocks - 1, 1,
            (ulong)usableStart, lastUsable, (ulong)totalBlocks - 33, entriesCrc);
        entries.CopyTo(image.Slice((totalBlocks - 33) * BlockSize, PartitionCount * PartitionEntrySize));
    }

    private static void WriteHeader(Span<byte> header, ulong myLba, ulong alternateLba, ulong firstUsable,
        ulong lastUsable, ulong entriesLba, uint entriesCrc)
    {
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], myLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], firstUsable);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], lastUsable);
        new Guid("15536006-38b1-315b-87dd-ba2df3034625").TryWriteBytes(header[56..], bigEndian: false, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], PartitionCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], PartitionEntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[88..], entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32Ieee.Compute(header[..92]));
    }

    private static void WritePartition(Span<byte> slot, string name)
    {
        switch (name)
        {
            case "boot-resource":
                WriteFatVolume(slot);
                break;
            case "env":
            case "env-redund":
                WriteEnvironment(slot);
                break;
            case "rootfs":
                "hsqs"u8.CopyTo(slot);
                break;
            case "boot":
                "ANDROID!"u8.CopyTo(slot);
                BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], 0x400);
                BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], 0x40200000);
                BinaryPrimitives.WriteUInt32LittleEndian(slot[44..], 2048);
                Encoding.ASCII.GetBytes("d1-nezha").CopyTo(slot[48..]);
                "RISCV"u8.CopyTo(slot[(2048 + 56)..]);
                break;
            case "dsp0":
                slot[0] = 0x7F;
                slot[1] = (byte)'E';
                slot[2] = (byte)'L';
                slot[3] = (byte)'F';
                slot[4] = 1;
                slot[5] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(slot[18..], 0xF3);
                break;
            default:
                Encoding.ASCII.GetBytes("blank.fex\n").CopyTo(slot);
                break;
        }
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