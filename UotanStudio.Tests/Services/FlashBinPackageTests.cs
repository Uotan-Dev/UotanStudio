using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashBinPackageTests
{
    [Fact]
    public void Read_FlatQualcommFolder_SeparatesImagesFromManifestAndProgrammer()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "system.bin"), [1]);
            File.WriteAllBytes(Path.Combine(root, "gpt_main0.bin"), [2]);
            File.WriteAllBytes(Path.Combine(root, "prog_ufs_firehose_test.elf"), [3]);
            File.WriteAllText(Path.Combine(root, "rawprogram0.xml"),
                """<data><program filename="system.bin" label="system_a" start_sector="8" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/></data>""");

            var package = FlashBinPackage.Read(root);

            var image = Assert.Single(package.Images);
            Assert.Equal("system_a", image.Partition);
            Assert.Equal("system.bin", Path.GetFileName(image.FilePath));
            Assert.Equal("system_a", Assert.Single(package.Programs).Label);
            Assert.Equal("prog_ufs_firehose_test.elf", Path.GetFileName(package.Programmer));
            Assert.Equal("prog_ufs_firehose_test.elf", Path.GetFileName(Assert.Single(package.Programmers)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_MultipleFirehoseLoaders_ReportsEveryCandidateWithoutChoosing()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "system.bin"), [1]);
            File.WriteAllBytes(Path.Combine(root, "prog_ufs_firehose_sm8250.elf"), [3]);
            File.WriteAllBytes(Path.Combine(root, "prog_firehose_256kb.elf"), [4]);

            var package = FlashBinPackage.Read(root);

            Assert.Null(package.Programmer);
            Assert.Equal(
                ["prog_firehose_256kb.elf", "prog_ufs_firehose_sm8250.elf"],
                package.Programmers.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_MultipleRawprograms_KeepsSeparateTargetsForSharedImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "system.bin"), [1]);
            File.WriteAllText(Path.Combine(root, "rawprogram0.xml"),
                """<data><program filename="system.bin" label="system_a" start_sector="8" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/></data>""");
            File.WriteAllText(Path.Combine(root, "rawprogram1.xml"),
                """<data><program filename="system.bin" label="system_b" start_sector="16" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="1"/></data>""");

            var package = FlashBinPackage.Read(root);

            Assert.Equal(["system_a", "system_b"], package.Images.Select(entry => entry.Partition));
            Assert.Equal([0, 1], package.Images.Select(entry => entry.Program.PhysicalPartitionNumber));
            Assert.Equal(["system_a", "system_b"], package.Programs.Select(entry => entry.Label));
            Assert.Equal([0, 1], package.Programs.Select(entry => entry.PhysicalPartitionNumber));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_ImagesSubdirWithMbnProgrammer_ListsReferencedImagesFromSubdir()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-bin-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        try
        {
            File.WriteAllText(Path.Combine(root, "flash_all.bat"), "fastboot flash boot boot.img");
            File.WriteAllBytes(Path.Combine(images, "sbl1.mbn"), [1]);
            File.WriteAllBytes(Path.Combine(images, "dummy.img"), [2]);
            File.WriteAllBytes(Path.Combine(images, "prog_emmc_firehose_8936.mbn"), [3]);
            File.WriteAllText(Path.Combine(images, "rawprogram0.xml"),
                """<data><program filename="sbl1.mbn" label="sbl1" start_sector="34" num_partition_sectors="4096" SECTOR_SIZE_IN_BYTES="512" physical_partition_number="0"/></data>""");

            var package = FlashBinPackage.Read(root);

            var image = Assert.Single(package.Images);
            Assert.Equal("sbl1", image.Partition);
            Assert.Equal("sbl1.mbn", Path.GetFileName(image.FilePath));
            Assert.Equal(1, image.Length);
            Assert.Equal("prog_emmc_firehose_8936.mbn", Path.GetFileName(package.Programmer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_GptAndMissingEntries_NeverBecomeRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "persist.img"), [1]);
            File.WriteAllBytes(Path.Combine(root, "gpt_main0.bin"), [2]);
            File.WriteAllText(Path.Combine(root, "rawprogram0.xml"),
                """
                <data>
                  <program filename="persist.img" label="persist" start_sector="2056" num_partition_sectors="8192" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="gpt_main0.bin" label="PrimaryGPT" start_sector="0" num_partition_sectors="6" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="absent.img" label="absent" start_sector="8" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="" label="ssd" start_sector="6" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                </data>
                """);

            var package = FlashBinPackage.Read(root);

            Assert.Equal("persist", Assert.Single(package.Images).Partition);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_EmptyLabel_FallsBackToImageStem()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "recovery.img"), [1]);
            File.WriteAllText(Path.Combine(root, "rawprogram0.xml"),
                """<data><program filename="recovery.img" label="" start_sector="2056" num_partition_sectors="8192" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/></data>""");

            var package = FlashBinPackage.Read(root);

            Assert.Equal("recovery", Assert.Single(package.Images).Partition);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}