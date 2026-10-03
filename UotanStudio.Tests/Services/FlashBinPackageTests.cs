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

            Assert.Equal("system", Assert.Single(package.Images).Partition);
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

            Assert.Single(package.Images);
            Assert.Equal(["system_a", "system_b"], package.Programs.Select(entry => entry.Label));
            Assert.Equal([0, 1], package.Programs.Select(entry => entry.PhysicalPartitionNumber));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}