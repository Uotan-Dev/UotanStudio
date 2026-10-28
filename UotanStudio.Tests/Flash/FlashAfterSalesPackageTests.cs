using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashAfterSalesPackageTests
{
    [Fact]
    public void Read_AfterSalesPackage_MapsPartitionsAndMergesRadioDuplicates()
    {
        var root = CreateRoot("flash-service-");
        try
        {
            var images = Directory.CreateDirectory(Path.Combine(root, "IMAGES")).FullName;
            Directory.CreateDirectory(Path.Combine(images, "my_bigball"));
            Directory.CreateDirectory(Path.Combine(root, "META"));
            var radio = Directory.CreateDirectory(Path.Combine(root, "RADIO")).FullName;
            File.WriteAllBytes(Path.Combine(images, "my_bigball", "my_bigball.27310181.img"), new byte[4]);
            File.WriteAllBytes(Path.Combine(images, "system.img"), new byte[8]);
            File.WriteAllBytes(Path.Combine(images, "userdata.img"), new byte[8]);
            File.WriteAllBytes(Path.Combine(images, "super_meta.raw"), new byte[8]);
            File.WriteAllBytes(Path.Combine(images, "vbmeta.img"), new byte[8]);
            File.WriteAllBytes(Path.Combine(images, "NON-HLOS.bin"), new byte[8]);
            File.WriteAllBytes(Path.Combine(images, "prog_firehose_ddr.elf"), new byte[8]);
            File.WriteAllBytes(Path.Combine(radio, "xbl.img"), new byte[8]);
            File.WriteAllBytes(Path.Combine(radio, "vbmeta.img"), new byte[8]);
            File.WriteAllText(Path.Combine(root, "META", "misc_info.txt"), "unused");
            File.WriteAllText(Path.Combine(images, "rawprogram0.xml"),
                """
                <data>
                  <program filename="system.img" label="system" start_sector="8" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="NON-HLOS.bin" label="modem" start_sector="16" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="userdata.img" label="userdata" start_sector="24" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                </data>
                """);
            File.WriteAllText(Path.Combine(images, "rawprogram0_BLANK_GPT.xml"),
                """<data><program filename="gpt_main0.bin" label="blankgpt" start_sector="0" num_partition_sectors="1"/></data>""");

            var package = FlashAfterSalesPackage.Read(root);

            Assert.NotNull(package);
            Assert.Equal(images, package.ImageDirectory);
            Assert.Equal(
                ["modem", "my_bigball", "system", "vbmeta", "xbl"],
                package.Images.Select(image => image.Partition).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(package.Images, image => image.Partition == "userdata");
            Assert.DoesNotContain(package.Images, image => image.Partition == "super_meta");
            Assert.Equal(1, package.MergedRadioImages);
            Assert.False(package.HasMergedSuper);
            Assert.Equal("prog_firehose_ddr.elf", Path.GetFileName(package.Programmer));
            var system = Assert.Single(package.Images, image => image.Partition == "system");
            Assert.NotNull(system.Program);
            Assert.Equal("system", system.Program.Label);
            Assert.Null(Assert.Single(package.Images, image => image.Partition == "xbl").Program);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_MergedSuperPackage_HidesScatteredDynamicPartitions()
    {
        var root = CreateRoot("flash-service-");
        try
        {
            var images = Directory.CreateDirectory(Path.Combine(root, "IMAGES")).FullName;
            Directory.CreateDirectory(Path.Combine(images, "my_bigball"));
            Directory.CreateDirectory(Path.Combine(root, "META"));
            File.WriteAllBytes(Path.Combine(images, "my_bigball", "my_bigball.1.img"), new byte[4]);
            File.WriteAllBytes(Path.Combine(images, "system.img"), new byte[8]);
            File.WriteAllBytes(Path.Combine(images, "super.img"), new byte[8]);

            var package = FlashAfterSalesPackage.Read(root);

            Assert.NotNull(package);
            Assert.True(package.HasMergedSuper);
            Assert.Equal(["super", "system"], package.Images.Select(image => image.Partition).Order(StringComparer.Ordinal));
            Assert.Equal(1, package.HiddenDynamicImages);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_PlainImageFolder_ReturnsNull()
    {
        var root = CreateRoot("flash-images-");
        try
        {
            var images = Directory.CreateDirectory(Path.Combine(root, "images")).FullName;
            File.WriteAllBytes(Path.Combine(images, "boot.img"), new byte[8]);

            Assert.False(FlashAfterSalesPackage.HasStructure(root));
            Assert.Null(FlashAfterSalesPackage.Read(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_SkipsProvisioningManifests()
    {
        var root = CreateRoot("flash-manifest-");
        try
        {
            foreach (var name in new[]
                     {
                         "rawprogram.xml", "rawprogram0.xml", "rawprogram5.xml",
                         "rawprogram0_BLANK_GPT.xml", "rawprogram5_WIPE_PARTITIONS.xml",
                         "wipe_rawprogram_PHY0.xml", "patch0.xml"
                     })
            {
                File.WriteAllText(Path.Combine(root, name), "<data/>");
            }

            var manifests = FlashManifestSelection.Enumerate(root);

            Assert.Equal(["rawprogram.xml", "rawprogram0.xml", "rawprogram5.xml"],
                manifests.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}