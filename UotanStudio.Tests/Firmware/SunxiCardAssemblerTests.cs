using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class SunxiCardAssemblerTests
{
    private static readonly Guid LinuxDataGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    [Fact]
    public void Build_WriterOutput_RoundTripsThroughCardParser()
    {
        using var temp = new TempDirectory();
        var loader = WriteLoader(temp);
        var payload = temp.WriteBytes("rootfs.fex", Pattern(4096, 7));
        var partitions = new List<SunxiCardWritePartition>
        {
            new("boot-resource", 34_816, 64, LinuxDataGuid, null),
            new("rootfs", 34_880, 128, LinuxDataGuid, payload),
        };

        var destination = Path.Combine(temp.Path, "card.img");
        var result = SunxiCardImageWriter.Build(
            new SunxiCardBuildInput(loader, null, 36_000), partitions, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(destination));
        Assert.Equal(36_000L * SunxiCardImageWriter.BlockSize, result.ImageBytes);
        Assert.Equal(2, result.PartitionCount);
        Assert.Null(result.BootPackageOffset);

        using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = SunxiCardImageFormat.Read(stream, TestContext.Current.CancellationToken);
        Assert.Equal(2, image.Partitions.Count);
        Assert.Equal("boot-resource", image.Partitions[0].Name);
        Assert.Equal("rootfs", image.Partitions[1].Name);
        Assert.Equal(34_816UL, image.Partitions[0].FirstLba);
        Assert.Equal(SunxiCardImageWriter.FirstUsableBlock, image.FirstUsableLba);
        Assert.NotNull(image.BootRegion.Boot0);
        Assert.Equal("eGON.BT0", image.BootRegion.Boot0!.Stamp);
    }

    [Fact]
    public void Build_PartitionInsideBootRegion_IsRejected()
    {
        using var temp = new TempDirectory();
        var loader = WriteLoader(temp);
        var partitions = new List<SunxiCardWritePartition>
        {
            new("too-early", 1_000, 64, LinuxDataGuid, null),
        };

        var error = Assert.Throws<InvalidDataException>(() => SunxiCardImageWriter.Build(
            new SunxiCardBuildInput(loader, null, 36_000), partitions,
            Path.Combine(temp.Path, "card.img"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("boot region", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_OverlappingPartitions_AreRejected()
    {
        using var temp = new TempDirectory();
        var loader = WriteLoader(temp);
        var partitions = new List<SunxiCardWritePartition>
        {
            new("first", 34_816, 128, LinuxDataGuid, null),
            new("second", 34_900, 128, LinuxDataGuid, null),
        };

        var error = Assert.Throws<InvalidDataException>(() => SunxiCardImageWriter.Build(
            new SunxiCardBuildInput(loader, null, 36_000), partitions,
            Path.Combine(temp.Path, "card.img"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_PayloadLargerThanSlot_IsRejected()
    {
        using var temp = new TempDirectory();
        var loader = WriteLoader(temp);
        var payload = temp.WriteBytes("big.fex", Pattern(4096, 9));
        var partitions = new List<SunxiCardWritePartition>
        {
            new("small", 34_816, 2, LinuxDataGuid, payload),
        };

        var error = Assert.Throws<InvalidDataException>(() => SunxiCardImageWriter.Build(
            new SunxiCardBuildInput(loader, null, 36_000), partitions,
            Path.Combine(temp.Path, "card.img"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("above its", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_FexDirectory_ProducesCardImageFromTable()
    {
        using var temp = new TempDirectory();
        var material = Path.Combine(temp.Path, "material");
        Directory.CreateDirectory(material);
        File.Copy(WriteLoader(temp), Path.Combine(material, SunxiCardMaterialReader.FirstStageLoaderFileName));
        File.WriteAllBytes(Path.Combine(material, "rootfs.fex"), Pattern(2048, 3));
        var table = BuildMbrTable(
            ("boot-resource", 34_816UL, 64UL),
            ("rootfs", 34_880UL, 128UL));
        File.WriteAllBytes(Path.Combine(material, SunxiCardMaterialReader.PartitionTableFileName), table);

        Assert.True(SunxiCardMaterialReader.IsFexDirectory(material));
        Assert.True(SunxiCardAssembler.IsSupported(material, TestContext.Current.CancellationToken));

        var destination = Path.Combine(temp.Path, "assembled.img");
        var result = SunxiCardAssembler.Assemble(material, destination,
            capacityBytes: null, progress: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SunxiCardMaterialKind.FexDirectory, result.Kind);
        Assert.Equal(2, result.PartitionCount);
        Assert.True(File.Exists(destination));

        using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = SunxiCardImageFormat.Read(stream, TestContext.Current.CancellationToken);
        Assert.Equal("boot-resource", image.Partitions[0].Name);
        Assert.Equal("rootfs", image.Partitions[1].Name);
        Assert.Equal(34_816UL, image.Partitions[0].FirstLba);
        Assert.True(image.Partitions[1].ContentLength > 0);
    }

    [Fact]
    public void Assemble_FexDirectoryWithoutLoader_IsRejected()
    {
        using var temp = new TempDirectory();
        var material = Path.Combine(temp.Path, "material");
        Directory.CreateDirectory(material);
        var table = BuildMbrTable(("rootfs", 34_816UL, 64UL));
        File.WriteAllBytes(Path.Combine(material, SunxiCardMaterialReader.PartitionTableFileName), table);

        var error = Assert.Throws<InvalidDataException>(() => SunxiCardAssembler.Assemble(material,
            Path.Combine(temp.Path, "assembled.img"), capacityBytes: null, progress: null,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("boot0_sdcard.fex", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Assemble_CapacityTooSmallForTable_IsRejected()
    {
        using var temp = new TempDirectory();
        var material = Path.Combine(temp.Path, "material");
        Directory.CreateDirectory(material);
        File.Copy(WriteLoader(temp), Path.Combine(material, SunxiCardMaterialReader.FirstStageLoaderFileName));
        File.WriteAllBytes(Path.Combine(material, SunxiCardMaterialReader.PartitionTableFileName),
            BuildMbrTable(("rootfs", 34_816UL, 4096UL)));

        var error = Assert.Throws<InvalidDataException>(() => SunxiCardAssembler.Assemble(material,
            Path.Combine(temp.Path, "assembled.img"), capacityBytes: 4096 * 512,
            progress: null, TestContext.Current.CancellationToken));
        Assert.Contains("cannot hold", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string WriteLoader(TempDirectory temp)
    {
        var bytes = new byte[4096];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x3C80006F);
        "eGON.BT0"u8.CopyTo(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 0x20000);
        Encoding.ASCII.GetBytes("4.0\0").CopyTo(bytes.AsSpan(48));
        return temp.WriteBytes("boot0_sdcard.fex", bytes);
    }

    private static byte[] BuildMbrTable(params (string Name, ulong FirstSector, ulong SectorCount)[] partitions)
    {
        var table = new byte[SunxiMbrTable.TableSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), 0x0002);
        SunxiMbrTable.Magic.CopyTo(table.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(24), (uint)partitions.Length);
        for (var index = 0; index < partitions.Length; index++)
        {
            var entry = table.AsSpan(SunxiMbrTable.HeaderSize + (index * SunxiMbrTable.PartitionEntrySize),
                SunxiMbrTable.PartitionEntrySize);
            var (name, firstSector, sectorCount) = partitions[index];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)(firstSector >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)firstSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)(sectorCount >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)sectorCount);
            Encoding.ASCII.GetBytes("DATA").CopyTo(entry[16..]);
            Encoding.ASCII.GetBytes(name).CopyTo(entry[32..]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[48..], 2);
        }

        return table;
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)((index + seed) & 0xFF);
        }

        return content;
    }
}