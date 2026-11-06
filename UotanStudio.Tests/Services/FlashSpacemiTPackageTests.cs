using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashSpacemiTPackageTests
{
    [Fact]
    public void HasStructure_FolderWithoutFastbootYaml_IsFalse()
    {
        var root = CreatePackageFolder();
        try
        {
            Assert.False(FlashSpacemiTPackage.HasStructure(root));
            Assert.Null(FlashSpacemiTPackage.Read(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_BianbuLayout_ResolvesUniversalTableRows()
    {
        var root = CreatePackageFolder();
        try
        {
            WriteScript(root);
            Directory.CreateDirectory(Path.Combine(root, "factory"));
            File.WriteAllBytes(Path.Combine(root, "factory", "FSBL.bin"), [1]);
            File.WriteAllBytes(Path.Combine(root, "factory", "bootinfo_sd.bin"), [2]);
            File.WriteAllBytes(Path.Combine(root, "env.bin"), [3]);
            File.WriteAllBytes(Path.Combine(root, "fw_dynamic.itb"), [4]);
            File.WriteAllBytes(Path.Combine(root, "u-boot.itb"), [5]);
            File.WriteAllBytes(Path.Combine(root, "bootfs.ext4"), [6, 6]);
            File.WriteAllBytes(Path.Combine(root, "rootfs.ext4"), [7]);
            File.WriteAllText(Path.Combine(root, "partition_universal.json"),
                """
                {
                  "version": "1.0",
                  "format": "gpt",
                  "partitions": [
                    { "name": "bootinfo", "hidden": true, "offset": "0K", "size": "512", "image": "factory/bootinfo_sd.bin" },
                    { "name": "fsbl", "offset": "128K", "size": "256K", "image": "factory/FSBL.bin" },
                    { "name": "env", "offset": "384K", "size": "64K", "image": "env.bin" },
                    { "name": "opensbi", "offset": "1M", "size": "1M", "image": "fw_dynamic.itb" },
                    { "name": "uboot", "offset": "2M", "size": "2M", "image": "u-boot.itb" },
                    { "name": "bootfs", "offset": "4M", "size": "256M", "image": "bootfs.ext4", "compress": "gzip-5" },
                    { "name": "rootfs", "offset": "260M", "size": "-", "image": "rootfs.ext4" },
                    { "name": "misc", "offset": "268M", "size": "4M" }
                  ]
                }
                """);

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            Assert.Equal(root, contents.Package.RootPath);
            Assert.EndsWith("partition_universal.json", contents.PartitionTablePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(8, contents.Partitions.Count);

            var bootinfo = contents.Partitions[0];
            Assert.Equal("bootinfo", bootinfo.Partition);
            Assert.NotNull(bootinfo.FilePath);
            Assert.Equal("bootinfo_sd.bin", Path.GetFileName(bootinfo.FilePath));
            Assert.Equal(0, bootinfo.OffsetBytes);
            Assert.Equal(512, bootinfo.SizeBytes);

            var bootfs = contents.Partitions.Single(entry => entry.Partition == "bootfs");
            Assert.Equal(4 * 1024 * 1024, bootfs.OffsetBytes);
            Assert.Equal(256L * 1024 * 1024, bootfs.SizeBytes);
            Assert.Equal(2, bootfs.FileLength);

            var rootfs = contents.Partitions.Single(entry => entry.Partition == "rootfs");
            Assert.Equal(260L * 1024 * 1024, rootfs.OffsetBytes);
            Assert.True(rootfs.SizeBytes < 0);
            Assert.NotNull(rootfs.FilePath);

            var misc = contents.Partitions.Single(entry => entry.Partition == "misc");
            Assert.Null(misc.FilePath);
            Assert.Equal(string.Empty, misc.ImageReference);
            Assert.Equal(["gpt"], misc.TableFormats);
            // The table gives this partition a place but no image, so the row writes nothing to storage and reports no
            // medium: naming one would describe a write the run never performs.
            // 分区表给该分区位置却不给镜像，因此该行不向存储写入任何内容，也不上报介质：标上介质等于描述一次运行不会执行的写入。
            Assert.Equal(string.Empty, misc.StorageMedium);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_MultipleTables_ListsBothLayoutsWithTheirStorageMedium()
    {
        var root = CreatePackageFolder();
        try
        {
            WriteScript(root);
            File.WriteAllBytes(Path.Combine(root, "env.bin"), [1]);
            File.WriteAllText(Path.Combine(root, "partition_2M.json"),
                """{"version":"1.0","format":"mtd","partitions":[{"name":"nor-env","offset":"0K","size":"64K","image":"env.bin"}]}""");
            File.WriteAllText(Path.Combine(root, "partition_universal.json"),
                """{"version":"1.0","format":"gpt","partitions":[{"name":"emmc-env","offset":"384K","size":"64K","image":"env.bin"}]}""");

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            Assert.Equal(["emmc-env", "nor-env"], contents.Partitions.Select(entry => entry.Partition));
            Assert.Equal(["gpt"], contents.Partitions[0].TableFormats);
            Assert.Equal("eMMC/SD", contents.Partitions[0].StorageMedium);
            Assert.Equal(["mtd"], contents.Partitions[1].TableFormats);
            Assert.Equal("SPI NOR", contents.Partitions[1].StorageMedium);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_PartitionOnBothMedia_IsOneRowCarryingBothMedia()
    {
        var root = CreatePackageFolder();
        try
        {
            WriteScript(root);
            Directory.CreateDirectory(Path.Combine(root, "factory"));
            File.WriteAllBytes(Path.Combine(root, "factory", "bootinfo_spinor.bin"), [1, 1]);
            File.WriteAllBytes(Path.Combine(root, "factory", "bootinfo_sd.bin"), [2, 2, 2]);
            File.WriteAllText(Path.Combine(root, "partition_2M.json"),
                """{"version":"1.0","format":"mtd","partitions":[{"name":"bootinfo","offset":"0","size":"128K","image":"factory/bootinfo_spinor.bin"}]}""");
            File.WriteAllText(Path.Combine(root, "partition_universal.json"),
                """{"version":"1.0","format":"gpt","partitions":[{"name":"bootinfo","offset":"0K","size":"512","image":"factory/bootinfo_sd.bin"}]}""");

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            var bootinfo = Assert.Single(contents.Partitions);
            Assert.Equal(["gpt", "mtd"], bootinfo.TableFormats);
            Assert.Equal("eMMC/SD + SPI NOR", bootinfo.StorageMedium);
            Assert.Equal("bootinfo_sd.bin", Path.GetFileName(bootinfo.FilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_FlashAllDescriptor_IsNotTreatedAsALayout()
    {
        var root = CreatePackageFolder();
        try
        {
            WriteScript(root);
            File.WriteAllBytes(Path.Combine(root, "boot.img"), [1]);
            File.WriteAllText(Path.Combine(root, "partition_flash.json"),
                """
                {
                  "version": "1.0",
                  "format": "gpt",
                  "partitions": [
                    { "name": "everything", "offset": "0K", "size": "-", "image": ["boot.img", "/partition_.+.json$/"] }
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(root, "partition_universal.json"),
                """{"version":"1.0","format":"gpt","partitions":[{"name":"boot", "offset":"4M","size":"256M","image":"boot.img"}]}""");

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            Assert.Equal(["boot"], contents.Partitions.Select(entry => entry.Partition));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_RegexAndMissingReferences_StayUnresolvedRows()
    {
        var root = CreatePackageFolder();
        try
        {
            WriteScript(root);
            File.WriteAllText(Path.Combine(root, "partition_universal.json"),
                """
                {
                  "version": "1.0",
                  "format": "gpt",
                  "partitions": [
                    { "name": "absent", "offset": "0K", "size": "64K", "image": "factory/missing.bin" },
                    { "name": "templated", "offset": "64K", "size": "64K", "image": "/partition_.+.json$/" }
                  ]
                }
                """);

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            Assert.Equal(2, contents.Partitions.Count);
            Assert.Null(contents.Partitions[0].FilePath);
            Assert.Equal("factory/missing.bin", contents.Partitions[0].ImageReference);
            Assert.Null(contents.Partitions[1].FilePath);
            Assert.Equal("/partition_.+.json$/", contents.Partitions[1].ImageReference);

            // A missing file still has the place the table gives it, so the medium stays; a vendor regex marker names
            // no file at all — the vendor tool builds that content in memory — so it reports no medium.
            // 缺失的文件仍占有分区表给定的位置，因此保留介质；厂商正则标记不指向任何文件——那类内容由厂商工具在内存中生成
            // ——因此不上报介质。
            Assert.Equal("eMMC/SD", contents.Partitions[0].StorageMedium);
            Assert.Equal(string.Empty, contents.Partitions[1].StorageMedium);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreatePackageFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-spacemit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Writes a minimal valid <c>fastboot.yaml</c> so package validation passes.
    /// <para>写入一个最小可解析的 <c>fastboot.yaml</c>，使包校验通过。</para></summary>
    private static void WriteScript(string root)
    {
        File.WriteAllText(Path.Combine(root, "fastboot.yaml"),
            """
            version: 1.0
            support:
              - 'k1x'
            actions:
              - getvar:
                  args: 'version-brom'
                  skip_fail: true
                  timeout:
                    seconds: 1
            """);
    }
}