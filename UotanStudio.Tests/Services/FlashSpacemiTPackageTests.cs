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
            // The table places misc on the block device and the run writes it from the file picked in its own cell,
            // so the row reports the medium its layout gives it.
            // 分区表把 misc 放在块设备上，运行会以用户在其文件列选择的文件写入，因此该行上报其布局给出的介质。
            Assert.Equal("eMMC/SD", misc.StorageMedium);
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

            // A missing file still has the place the table gives it, so the medium stays; a vendor regex marker is
            // resolved to a package file before the run writes it, so it reports the same medium.
            // 缺失的文件仍占有分区表给定的位置，因此保留介质；厂商正则标记在运行写入前解析为包内文件，因此上报同样的介质。
            Assert.Equal("eMMC/SD", contents.Partitions[0].StorageMedium);
            Assert.Equal("eMMC/SD", contents.Partitions[1].StorageMedium);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_NandUserVolumeImages_ListsSynthesizedVolumeRows()
    {
        var root = CreatePackageFolder();
        try
        {
            WriteScript(root);
            File.WriteAllBytes(Path.Combine(root, "env.bin"), [1]);
            File.WriteAllBytes(Path.Combine(root, "bootfs.img"), [2, 2]);
            File.WriteAllBytes(Path.Combine(root, "rootfs.img"), [3, 3, 3]);
            File.WriteAllText(Path.Combine(root, "partition_universal.json"),
                """
                {
                  "version": "1.0",
                  "format": "gpt",
                  "partitions": [
                    { "name": "env", "offset": "384K", "size": "64K", "image": "env.bin" },
                    { "name": "user", "offset": "1M", "size": "-", "volume_images": { "bootfs": "bootfs.img", "rootfs": "rootfs.img" } }
                  ]
                }
                """);

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            Assert.Equal(["env", "user", "user-bootfs", "user-rootfs"],
                contents.Partitions.Select(entry => entry.Partition));

            var env = contents.Partitions.Single(entry => entry.Partition == "env");
            Assert.Equal("env.bin", env.ImageReference);
            Assert.NotNull(env.FilePath);

            var user = contents.Partitions.Single(entry => entry.Partition == "user");
            Assert.Equal(string.Empty, user.ImageReference);
            Assert.Null(user.FilePath);

            var bootfs = contents.Partitions.Single(entry => entry.Partition == "user-bootfs");
            Assert.Equal("bootfs.img", bootfs.ImageReference);
            Assert.Equal("bootfs.img", Path.GetFileName(bootfs.FilePath));
            Assert.Equal(2, bootfs.FileLength);
            Assert.Equal("eMMC/SD", bootfs.StorageMedium);

            var rootfs = contents.Partitions.Single(entry => entry.Partition == "user-rootfs");
            Assert.Equal("rootfs.img", rootfs.ImageReference);
            Assert.Equal("rootfs.img", Path.GetFileName(rootfs.FilePath));
            Assert.Equal(3, rootfs.FileLength);
            Assert.Equal("eMMC/SD", rootfs.StorageMedium);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_ScriptSteps_DeriveStageTransfersAndStoragePhaseOem()
    {
        var root = CreatePackageFolder();
        try
        {
            // The vendor K3 shape: two staging phases, a boot-phase speed switch, then the EC write that runs in the
            // storage phase after the last continue.
            // 厂商 K3 形态：两个暂存阶段、引导段落的速率切换，然后是最后一个 continue 之后在存储段落执行的 EC 写入。
            File.WriteAllText(Path.Combine(root, "fastboot.yaml"),
                """
                version: 1.0
                support:
                  - 'k3'
                actions:
                  - getvar:
                      args: 'version-brom'
                      set: 'version'
                      skip_fail: true
                  - stage:
                      file: 'factory/FSBL.bin'
                      skip_when: "not temp.version"
                  - continue:
                      skip_when: "not temp.version"
                  - oem:
                      args: 'speed:super-speed'
                      skip_when: "not temp.version"
                      skip_fail: true
                  - stage:
                      file: 'u-boot.itb'
                  - continue: {}
                  - stage:
                      file: 'ec.bin'
                      skip_fail: true
                  - oem:
                      args: 'ec:flash'
                      skip_fail: true
                  - getvar:
                      args: 'mtd-size'
                      set_var: 'size0'
                      skip_fail: true
                  - multi_flash:
                      relate_partition: ['partition_{size0}.json']
                """);
            File.WriteAllText(Path.Combine(root, "partition_universal.json"),
                """{"version":"1.0","format":"gpt","partitions":[{"name":"bootfs","offset":"4M","size":"256M","image":"bootfs.ext4"}]}""");
            Directory.CreateDirectory(Path.Combine(root, "factory"));
            File.WriteAllBytes(Path.Combine(root, "factory", "FSBL.bin"), [1]);
            File.WriteAllBytes(Path.Combine(root, "u-boot.itb"), [2, 2]);
            File.WriteAllBytes(Path.Combine(root, "ec.bin"), [3, 3, 3]);
            File.WriteAllBytes(Path.Combine(root, "bootfs.ext4"), [4]);

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            Assert.Equal(4, contents.ScriptSteps.Count);

            var fsbl = contents.ScriptSteps[0];
            Assert.Equal("stage", fsbl.Command);
            Assert.Equal("factory/FSBL.bin", fsbl.FileReference);
            Assert.NotNull(fsbl.FilePath);
            Assert.Equal(1, fsbl.FileLength);
            // A stage transfer lands in device RAM, never in storage.
            // stage 传输落在设备内存，绝不写存储。
            Assert.Equal("MEMORY", fsbl.StorageMedium);

            var uboot = contents.ScriptSteps[1];
            Assert.Equal("stage", uboot.Command);
            Assert.Equal("u-boot.itb", uboot.FileReference);
            Assert.Equal(2, uboot.FileLength);
            Assert.Equal("MEMORY", uboot.StorageMedium);

            // The boot-phase speed switch is not a device-facing write and stays out of the rows.
            // 引导段落的速率切换不是面向设备的写入，不进入行列表。
            var ec = contents.ScriptSteps[2];
            Assert.Equal("stage", ec.Command);
            Assert.Equal("ec.bin", ec.FileReference);
            Assert.Equal(3, ec.FileLength);
            Assert.Equal("MEMORY", ec.StorageMedium);

            var ecFlash = contents.ScriptSteps[3];
            // The OEM body is part of the command itself and has no column of its own.
            // OEM 命令体是命令本身的一部分，没有属于自己的列。
            Assert.Equal("oem ec:flash", ecFlash.Command);
            Assert.Equal(string.Empty, ecFlash.FileReference);
            Assert.Null(ecFlash.FilePath);
            Assert.Equal(0, ecFlash.FileLength);
            Assert.Equal("EC", ecFlash.StorageMedium);

            Assert.Single(contents.Partitions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_ScriptWithoutContinue_KeepsEveryOemStep()
    {
        var root = CreatePackageFolder();
        try
        {
            File.WriteAllText(Path.Combine(root, "fastboot.yaml"),
                """
                version: 1.0
                support:
                  - 'k1x'
                actions:
                  - stage:
                      file: 'u-boot.itb'
                  - oem:
                      args: 'ec:flash'
                """);

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            // Without a continue the whole script is the storage phase, so the oem command is a step too.
            // 没有 continue 时整个脚本都是存储段落，因此 oem 命令同样是步骤。
            Assert.Equal(["stage", "oem ec:flash"],
                contents.ScriptSteps.Select(step => step.Command));
            Assert.Equal(["MEMORY", "EC"],
                contents.ScriptSteps.Select(step => step.StorageMedium));
            Assert.All(contents.ScriptSteps, step => Assert.Null(step.FilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_EntryWithPlainImage_KeepsSingleRowAndIgnoresVolumeImages()
    {
        var root = CreatePackageFolder();
        try
        {
            WriteScript(root);
            File.WriteAllBytes(Path.Combine(root, "system.img"), [1, 1]);
            File.WriteAllBytes(Path.Combine(root, "bootfs.img"), [2]);
            File.WriteAllText(Path.Combine(root, "partition_universal.json"),
                """
                {
                  "version": "1.0",
                  "format": "mtd",
                  "partitions": [
                    { "name": "system", "offset": "0", "size": "256M", "image": "system.img", "volume_images": { "bootfs": "bootfs.img" } }
                  ]
                }
                """);

            var contents = FlashSpacemiTPackage.Read(root);

            Assert.NotNull(contents);
            Assert.Equal(["system"], contents.Partitions.Select(entry => entry.Partition));
            var system = contents.Partitions[0];
            Assert.Equal("system.img", system.ImageReference);
            Assert.Equal("system.img", Path.GetFileName(system.FilePath));
            Assert.Equal("SPI NOR", system.StorageMedium);
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