using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashPackageParsingTests
{
    [Fact]
    public void FormatByteSize_ReturnsExpectedUnit()
    {
        Assert.Equal(string.Empty, FlashViewModel.FormatByteSize(0));
        Assert.Equal("512 B", FlashViewModel.FormatByteSize(512));
        Assert.Equal("2.0 KB", FlashViewModel.FormatByteSize(2048));
        Assert.Equal("10.0 MB", FlashViewModel.FormatByteSize(10 * 1024 * 1024));
        Assert.Equal("1.00 GB", FlashViewModel.FormatByteSize(1024L * 1024 * 1024));
        Assert.Equal("2.50 GB", FlashViewModel.FormatByteSize((long)(2.5 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void ParseScript_BatchScriptWithNonFastbootLines_ParsesOnlyFastbootCommands()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-script-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            var bootImg = Path.Combine(imagesDir, "boot.img");
            File.WriteAllBytes(bootImg, new byte[1024]);
            var superEmptyImg = Path.Combine(imagesDir, "super_empty.img");
            File.WriteAllBytes(superEmptyImg, new byte[512]);

            var scriptPath = Path.Combine(tempDir, "flash_all.bat");
            var lines = new[]
            {
                "@echo off",
                "echo Flashing images...",
                "fastboot %* erase metadata",
                "@fastboot %* flash boot \"%~dp0images\\boot.img\"",
                "fastboot %* wipe-super \"%~dp0images\\super_empty.img\"",
                "fastboot %* set_active a",
                "pause",
                "fastboot %* reboot"
            };

            var rows = FlashViewModel.ParseScript(scriptPath, lines);

            Assert.Equal(5, rows.Count);

            Assert.Equal("erase", rows[0].Command);
            Assert.Equal("metadata", rows[0].Name);

            Assert.Equal("flash", rows[1].Command);
            Assert.Equal("boot", rows[1].Name);
            Assert.Equal("boot.img", rows[1].FileName);
            Assert.Equal(bootImg, rows[1].FilePath);
            Assert.Equal("1.0 KB", rows[1].Size);

            Assert.Equal("wipe_super", rows[2].Command);
            Assert.Equal("super_empty", rows[2].Name);
            Assert.Equal("super_empty.img", rows[2].FileName);

            Assert.Equal("set_active", rows[3].Command);
            Assert.Equal("a", rows[3].Name);

            Assert.Equal("reboot", rows[4].Command);
            Assert.Equal("reboot", rows[4].Name);

            // A write and a super wipe both take an image, so those two rows may offer the choose-file prompt when the
            // image is missing; the other three keep their file cell blank rather than asking for a file their command
            // never reads. None of them prompts here, because every row already names its file.
            // 写入与 super 擦除都需要镜像，因此这两行在镜像缺失时才可能提示选择文件；其余三行的文件列保持空白，
            // 而不是索要命令根本不会读取的文件。此处没有一行提示，因为它们都已经有文件名。
            Assert.True(rows[1].AcceptsFile);
            Assert.True(rows[2].AcceptsFile);
            foreach (var row in new[] { rows[0], rows[3], rows[4] })
            {
                Assert.False(row.AcceptsFile, row.Name);
            }

            Assert.All(rows, row => Assert.False(row.CanChooseFile, row.Name));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseScript_ShellScript_NormalizesDirnameVariables()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-sh-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            var bootImg = Path.Combine(imagesDir, "boot.img");
            File.WriteAllBytes(bootImg, new byte[2048]);

            var scriptPath = Path.Combine(tempDir, "flash_all.sh");
            var lines = new[]
            {
                "#!/bin/bash",
                "echo \"Starting flash\"",
                "fastboot $* flash boot `dirname $0`/images/boot.img",
                "fastboot delete-logical-partition product_a",
                "fastboot create-logical-partition product_a 0"
            };

            var rows = FlashViewModel.ParseScript(scriptPath, lines);

            Assert.Equal(3, rows.Count);
            Assert.Equal("flash", rows[0].Command);
            Assert.Equal("boot", rows[0].Name);
            Assert.Equal(bootImg, rows[0].FilePath);

            Assert.Equal("delete", rows[1].Command);
            Assert.Equal("product_a", rows[1].Name);

            Assert.Equal("create", rows[2].Command);
            Assert.Equal("product_a", rows[2].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseManifestText_ValidTextWithHeadersAndCommands_ExtractsRowsAndMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-txt-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            var bootImg = Path.Combine(imagesDir, "boot.img");
            File.WriteAllBytes(bootImg, new byte[4096]);

            var txtPath = Path.Combine(tempDir, "flash_fastbootd.txt");
            var lines = new[]
            {
                "# Comments should be ignored",
                "codename: raphael",
                "mode:fastbootd",
                "",
                "system delete",
                "system create",
                "boot",
                "vendor_boot images/vendor_boot.img"
            };

            var rows = FlashViewModel.ParseManifestText(txtPath, lines, out var codeName, out var requiresFastbootd);

            Assert.Equal("raphael", codeName);
            Assert.True(requiresFastbootd);
            Assert.Equal(4, rows.Count);

            Assert.Equal("delete", rows[0].Command);
            Assert.Equal("system", rows[0].Name);

            Assert.Equal("create", rows[1].Command);
            Assert.Equal("system", rows[1].Name);

            Assert.Equal("flash", rows[2].Command);
            Assert.Equal("boot", rows[2].Name);
            Assert.Equal("boot.img", rows[2].FileName);
            Assert.Equal(bootImg, rows[2].FilePath);
            Assert.Equal("4.0 KB", rows[2].Size);

            Assert.Equal("flash", rows[3].Command);
            Assert.Equal("vendor_boot", rows[3].Name);
            Assert.Equal("vendor_boot.img", rows[3].FileName);

            // A manifest row that names an image already shows that name even when the file is missing, so its cell
            // never offers a prompt next to a size; the two structural commands offer no file at all.
            // 清单行一旦给出镜像名，即使文件缺失也显示该名称，因此单元格不会在大小旁边再提示选择文件；两条结构性命令
            // 则完全不提供文件。
            Assert.True(rows[2].AcceptsFile);
            Assert.False(rows[2].CanChooseFile);
            Assert.True(rows[3].AcceptsFile);
            Assert.False(rows[3].CanChooseFile);
            Assert.False(rows[0].AcceptsFile);
            Assert.False(rows[1].AcceptsFile);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <remarks>
    /// The directory also holds <c>flash.bat</c>, which must be skipped and never listed as a flashable row.
    /// <para>目录里还放了 <c>flash.bat</c>，它必须被跳过，绝不列为可刷写行。</para>
    /// </remarks>
    [Fact]
    public void ParseDirectory_DirectoryWithImagesSubdir_EnumeratesImagesExcludingScripts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-dir-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            File.WriteAllBytes(Path.Combine(imagesDir, "boot.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(imagesDir, "super_empty.img"), new byte[512]);
            File.WriteAllBytes(Path.Combine(imagesDir, "flash.bat"), new byte[100]);

            var rows = FlashViewModel.ParseDirectory(tempDir);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Name == "boot" && r.Command == "flash");
            Assert.Contains(rows, r => r.Name == "super_empty" && r.Command == "wipe_super");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <remarks>
    /// The manifest supplies the medium and the start sector the listing previously lost, and slot siblings stay
    /// adjacent in the order their first slot occupies on the storage.
    /// <para>清单提供此前丢失的介质与起始扇区；槽位兄弟项按首个槽位在存储上的位置相邻排列。</para>
    /// </remarks>
    [Fact]
    public void ParseDirectory_RawprogramManifest_RestoresMediumAndOffsetAndKeepsSlotSiblingsAdjacent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-dir-offset-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            File.WriteAllBytes(Path.Combine(imagesDir, "boot_a.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(imagesDir, "boot_b.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(imagesDir, "dtbo.img"), new byte[1024]);
            File.WriteAllText(Path.Combine(imagesDir, "rawprogram0.xml"),
                """
                <data>
                  <program filename="boot_a.img" label="boot_a" start_sector="1000" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="boot_b.img" label="boot_b" start_sector="2000" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="dtbo.img" label="dtbo" start_sector="3000" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                </data>
                """);

            var rows = FlashViewModel.ParseDirectory(tempDir);

            Assert.Equal(["boot_a", "boot_b", "dtbo"], rows.Select(row => row.Name));
            Assert.All(rows, row => Assert.Equal("LUN 0", row.StorageMedium));
            Assert.Equal("3.9 MB", rows[0].Offset);
            Assert.Equal("7.8 MB", rows[1].Offset);
            Assert.Equal("11.7 MB", rows[2].Offset);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <remarks>
    /// Rows group by storage medium first, then by the start sector inside that medium, so a LUN 0 partition at a
    /// later address never precedes an earlier one and LUN 1 never interleaves with LUN 0.
    /// <para>行先按存储介质分组，再按介质内起始扇区排列，使 LUN 0 中靠后的分区不会跑到靠前的分区之前，
    /// LUN 1 也不会与 LUN 0 交错。</para>
    /// </remarks>
    [Fact]
    public void ParseDirectory_MultipleLuns_OrdersByMediumThenStartSector()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-dir-lun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "boot.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(tempDir, "dtbo.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(tempDir, "system.bin"), new byte[1024]);
            File.WriteAllText(Path.Combine(tempDir, "rawprogram0.xml"),
                """
                <data>
                  <program filename="boot.img" label="boot" start_sector="900" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="dtbo.img" label="dtbo" start_sector="100" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                </data>
                """);
            File.WriteAllText(Path.Combine(tempDir, "rawprogram1.xml"),
                """
                <data>
                  <program filename="system.bin" label="system" start_sector="500" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="1"/>
                </data>
                """);

            var rows = FlashViewModel.ParseDirectory(tempDir);

            Assert.Equal(["dtbo", "boot", "system"], rows.Select(row => row.Name));
            Assert.Equal(["LUN 0", "LUN 0", "LUN 1"], rows.Select(row => row.StorageMedium));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <remarks>
    /// A dual-slot package may place slot B on another LUN; the medium block and its physical address order take
    /// priority, so slot B stays with LUN 1 instead of being pulled next to slot A in LUN 0.
    /// <para>双槽包可能把 B 槽放在另一个 LUN；介质块与其物理地址顺序优先，因此 B 槽留在 LUN 1，而不会被拉到
    /// LUN 0 的 A 槽旁边。</para>
    /// </remarks>
    [Fact]
    public void ParseDirectory_SlotPairAcrossMedia_SortsByMediumBeforeSlotAdjacency()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-dir-slot-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "boot_a.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(tempDir, "boot_b.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(tempDir, "dtbo.img"), new byte[1024]);
            File.WriteAllText(Path.Combine(tempDir, "rawprogram0.xml"),
                """
                <data>
                  <program filename="boot_a.img" label="boot_a" start_sector="1000" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="dtbo.img" label="dtbo" start_sector="5000" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                </data>
                """);
            File.WriteAllText(Path.Combine(tempDir, "rawprogram1.xml"),
                """
                <data>
                  <program filename="boot_b.img" label="boot_b" start_sector="1500" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="1"/>
                </data>
                """);

            var rows = FlashViewModel.ParseDirectory(tempDir);

            Assert.Equal(["boot_a", "dtbo", "boot_b"], rows.Select(row => row.Name));
            Assert.Equal(["LUN 0", "LUN 0", "LUN 1"], rows.Select(row => row.StorageMedium));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <remarks>
    /// Partitions addressed from the disk end must keep their relative order: a larger distance from the end sits
    /// earlier on the storage, and every absolute address still sorts ahead of them.
    /// <para>以磁盘末尾为基准的分区必须保持相对先后：距末尾越远在存储上越靠前，而所有绝对地址仍排在它们之前。</para>
    /// </remarks>
    [Fact]
    public void ParseDirectory_RelativeStartSectors_KeepTheirStorageOrder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-dir-relative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "boot.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(tempDir, "dtbo.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(tempDir, "misc.img"), new byte[1024]);
            File.WriteAllText(Path.Combine(tempDir, "rawprogram0.xml"),
                """
                <data>
                  <program filename="boot.img" label="boot" start_sector="5000" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="dtbo.img" label="dtbo" start_sector="NUM_DISK_SECTORS-100." num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                  <program filename="misc.img" label="misc" start_sector="NUM_DISK_SECTORS-200." num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/>
                </data>
                """);

            var rows = FlashViewModel.ParseDirectory(tempDir);

            Assert.Equal(["boot", "misc", "dtbo"], rows.Select(row => row.Name));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ClassifyFolder_ChoosesParserByMarkerFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-classify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var edl = Path.Combine(tempDir, "edl");
            Directory.CreateDirectory(edl);
            File.WriteAllText(Path.Combine(edl, "rawprogram0.xml"), "<data />");

            var scatter = Path.Combine(tempDir, "scatter");
            Directory.CreateDirectory(scatter);
            var scatterFile = Path.Combine(scatter, "MT6580_Android_scatter.txt");
            File.WriteAllText(scatterFile, "MTK_PLATFORM: MT6580");

            var script = Path.Combine(tempDir, "script");
            Directory.CreateDirectory(script);
            File.WriteAllBytes(Path.Combine(script, "boot.img"), new byte[64]);
            File.WriteAllText(Path.Combine(script, "flash_all.bat"), "fastboot flash boot boot.img");

            var images = Path.Combine(tempDir, "images-only");
            Directory.CreateDirectory(images);
            File.WriteAllBytes(Path.Combine(images, "boot.img"), new byte[64]);

            var vendor = Path.Combine(tempDir, "vendor");
            Directory.CreateDirectory(vendor);
            var vendorFile = Path.Combine(vendor, "package.nb0");
            File.WriteAllBytes(vendorFile, new byte[64]);

            Assert.Equal(FlashViewModel.FlashFolderKind.FlatEdl, FlashViewModel.ClassifyFolder(edl).Kind);
            Assert.Equal(FlashViewModel.FlashFolderKind.MtkScatter, FlashViewModel.ClassifyFolder(scatter).Kind);
            Assert.Equal(scatterFile, FlashViewModel.ClassifyFolder(scatter).Document);
            Assert.Equal(FlashViewModel.FlashFolderKind.Script, FlashViewModel.ClassifyFolder(script).Kind);
            Assert.Equal(FlashViewModel.FlashFolderKind.Images, FlashViewModel.ClassifyFolder(images).Kind);
            Assert.Equal(FlashViewModel.FlashFolderKind.VendorContainer, FlashViewModel.ClassifyFolder(vendor).Kind);
            Assert.Equal(vendorFile, FlashViewModel.ClassifyFolder(vendor).Document);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}