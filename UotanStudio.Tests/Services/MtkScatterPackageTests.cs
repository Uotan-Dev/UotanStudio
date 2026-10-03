using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkScatterPackageTests
{
    private static string Scatter(string fileName, bool download = true, string blockSize = "0x20000") => $$"""
        - general: MTK_PLATFORM_CFG
          info:
          - config_version: V2.1.0
            platform: MT6785
            project: test
            storage: EMMC
            boot_channel: test
            block_size: {{blockSize}}
        - partition_index: SYS2
          partition_name: boot
          file_name: {{fileName}}
          is_download: {{(download ? "true" : "false")}}
          type: NORMAL_ROM
          linear_start_addr: 20480
          physical_start_addr: 20480
          partition_size: 2048
          region: EMMC_USER
          storage: HW_STORAGE_EMMC
          boundary_check: true
          is_reserved: false
          operation_type: UPDATE
          is_upgradable: true
          empty_boot_needed: false
          reserve: 0x00
        - partition_index: SYS0
          partition_name: preloader
          file_name: NONE
          is_download: true
          type: SV5_BL_BIN
          linear_start_addr: 0
          physical_start_addr: 0
          partition_size: 524288
          region: unsupported-boot-region
          storage: HW_STORAGE_EMMC
          boundary_check: true
          is_reserved: false
          operation_type: BOOTLOADERS
        """;

    [Fact]
    public void Resolve_MissingImage_YieldsNoRows()
    {
        using var dir = new TempDirectory();
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));

        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        Assert.Equal("EMMC", resolution.StorageLabel);
        Assert.Empty(resolution.Rows);
    }

    [Fact]
    public void Resolve_PresentDownloadableImage_KeepsOnlyEligibleRow()
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[512]);
        dir.WriteBytes("preloader.img", new byte[256]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));

        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        var row = Assert.Single(resolution.Rows);
        Assert.Equal("boot", row.Partition.Name);
        Assert.Equal("SYS2", row.Partition.Index);
        Assert.Equal(512, row.Length);
    }

    [Fact]
    public void Resolve_DownloadDisabled_DropsRow()
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[512]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img", download: false));

        Assert.Empty(MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter)).Rows);
    }

    [Fact]
    public void Resolve_EscapingFileName_DropsRow()
    {
        using var dir = new TempDirectory();
        using var outside = new TempDirectory();
        File.WriteAllBytes(Path.Combine(outside.Path, "boot.img"), new byte[512]);
        var scatter = dir.Write("scatter.xml", Scatter("../" + Path.GetFileName(outside.Path) + "/boot.img"));

        Assert.Empty(MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter)).Rows);
    }

    [Fact]
    public void CreateStep_ResolvableRow_CarriesManifestIndexAndVerifyFlag()
    {
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", new byte[512]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        var step = MtkScatterPackage.CreateStep(resolution, 0, verify: true);

        Assert.Equal("SYS2", step.PartitionIndex);
        Assert.Equal(MtkFlashStepKind.Raw, step.Kind);
        Assert.Equal(512, step.Length);
        Assert.Equal(image, step.SourcePath);
        Assert.True(step.Verify);
    }

    [Fact]
    public void CreateStep_RowIndexOutOfRange_Throws()
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[512]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        Assert.Throws<ArgumentOutOfRangeException>(() => MtkScatterPackage.CreateStep(resolution, 1, verify: false));
    }

    [Fact]
    public void GetPhysicalPartition_StorageFamily_MatchesPlanReconciliation()
    {
        Assert.Equal(8u, MtkScatterPackage.GetPhysicalPartition(MtkXFlashStorageKind.Emmc));
        Assert.Equal(3u, MtkScatterPackage.GetPhysicalPartition(MtkXFlashStorageKind.Ufs));
        Assert.Throws<ArgumentOutOfRangeException>(() => MtkScatterPackage.GetPhysicalPartition(MtkXFlashStorageKind.Nor));
    }

    [Fact]
    public void Read_OversizedFile_RejectsBeforeParsing()
    {
        using var dir = new TempDirectory();
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        File.AppendAllText(scatter, new string(' ', checked((int)MtkScatterPackage.MaxScatterBytes) + 1));

        Assert.Throws<InvalidDataException>(() => MtkScatterPackage.Read(scatter));
    }

    private static string EraseScatter() => """
        - general: MTK_PLATFORM_CFG
          info:
          - config_version: V2.1.0
            platform: MT6785
            project: test
            storage: EMMC
            boot_channel: test
            block_size: 0x20000
        - partition_index: SYS2
          partition_name: boot
          file_name: boot.img
          is_download: true
          type: NORMAL_ROM
          linear_start_addr: 20480
          physical_start_addr: 20480
          partition_size: 2048
          region: EMMC_USER
          storage: HW_STORAGE_EMMC
          boundary_check: true
          is_reserved: false
          operation_type: UPDATE
          is_upgradable: true
          empty_boot_needed: false
          reserve: 0x00
        - partition_index: SYS3
          partition_name: userdata
          file_name: NONE
          is_download: false
          type: EXT4_IMG
          linear_start_addr: 40960
          physical_start_addr: 40960
          partition_size: 65536
          region: EMMC_USER
          storage: HW_STORAGE_EMMC
          boundary_check: true
          is_reserved: false
          operation_type: UPDATE
          is_upgradable: true
          empty_boot_needed: false
          reserve: 0x00
        - partition_index: SYS4
          partition_name: nvram
          file_name: NONE
          is_download: false
          type: NORMAL_ROM
          linear_start_addr: 61440
          physical_start_addr: 61440
          partition_size: 1024
          region: EMMC_USER
          storage: HW_STORAGE_EMMC
          boundary_check: true
          is_reserved: true
          operation_type: UPDATE
          is_upgradable: true
          empty_boot_needed: false
          reserve: 0x00
        - partition_index: SYS5
          partition_name: cache
          file_name: NONE
          is_download: false
          type: EXT4_IMG
          linear_start_addr: 81920
          physical_start_addr: 81920
          partition_size: 4096
          region: EMMC_USER
          storage: HW_STORAGE_EMMC
          boundary_check: true
          is_reserved: false
          operation_type: UPDATE
          is_upgradable: true
          empty_boot_needed: false
          reserve: 0x01
        """;

    private static MtkScatterStorage EraseLayout()
    {
        using var dir = new TempDirectory();
        var scatter = dir.Write("scatter.xml", EraseScatter());
        return MtkScatterPackage.Read(scatter).Storages![0];
    }

    [Fact]
    public void ListEraseTargets_KeepsImagelessUserRegionEntriesAndDropsReservedOrNonZeroReserve()
    {
        var targets = MtkScatterPackage.ListEraseTargets(EraseLayout());

        var names = targets.Select(partition => partition.Name).ToArray();
        Assert.Contains("boot", names);
        Assert.Contains("userdata", names);
        Assert.DoesNotContain("nvram", names);
        Assert.DoesNotContain("cache", names);
    }

    [Fact]
    public void ListEraseTargets_BootRegionStorage_DropsEveryEntry()
    {
        var source = EraseLayout();
        var layout = source with
        {
            Name = "EMMC",
            Partitions = [source.Partitions[0] with { Region = "EMMC_BOOT1" }]
        };

        Assert.Empty(MtkScatterPackage.ListEraseTargets(layout));
    }

    [Fact]
    public void ListEraseTargets_NullStorage_Throws()
        => Assert.Throws<ArgumentNullException>(() => MtkScatterPackage.ListEraseTargets(null!));

    [Fact]
    public void CreateEraseStep_EraseEligibleEntry_CarriesNameAndDeclaredCapacity()
    {
        var userdata = EraseLayout().Partitions.Single(partition => partition.Name == "userdata");

        var step = MtkScatterPackage.CreateEraseStep(userdata);

        Assert.Equal(MtkFlashStepKind.Erase, step.Kind);
        Assert.Equal("userdata", step.PartitionName);
        Assert.Equal(0, step.Offset);
        Assert.Equal(65536, step.Length);
        Assert.Null(step.SourcePath);
        Assert.Equal(0, step.SourceOffset);
        Assert.False(step.Verify);
    }

    [Fact]
    public void CreateEraseStep_UnusableCapacityOrMissingName_Throws()
    {
        var layout = EraseLayout();
        var zeroSize = layout.Partitions[1] with { Size = 0 };
        var noName = layout.Partitions[1] with { Name = " " };

        Assert.Throws<InvalidDataException>(() => MtkScatterPackage.CreateEraseStep(zeroSize));
        Assert.Throws<InvalidDataException>(() => MtkScatterPackage.CreateEraseStep(noName));
        Assert.Throws<ArgumentNullException>(() => MtkScatterPackage.CreateEraseStep(null!));
    }

    [Fact]
    public void CreateLegacyStep_UnalignedImage_RoundsExtentUpAndPadsTail()
    {
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", new byte[600]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        var step = MtkScatterPackage.CreateLegacyStep(resolution, 0, verify: true);

        Assert.Equal("boot", step.PartitionName);
        Assert.Equal(MtkFlashStepKind.Raw, step.Kind);
        Assert.Equal(0, step.Offset);
        Assert.Equal(1024, step.Length);
        Assert.Equal(image, step.SourcePath);
        Assert.Equal(0, step.SourceOffset);
        Assert.True(step.Verify);
        Assert.True(step.PadSourceTail);
    }

    [Fact]
    public void CreateLegacyStep_SparseContainer_RejectsRawWrite()
    {
        using var dir = new TempDirectory();
        var raw = dir.WriteBytes("raw.img", new byte[4096]);
        using (var sparse = SparseReader.FromRawFile(raw))
        using (var destination = new FileStream(Path.Combine(dir.Path, "boot.img"), FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            SparseWriter.WriteToStream(sparse, destination, includeCrc: true,
                cancellationToken: TestContext.Current.CancellationToken);
        }
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        var error = Assert.Throws<NotSupportedException>(
            () => MtkScatterPackage.CreateLegacyStep(resolution, 0, verify: false));

        Assert.Contains("sparse image", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateLegacyEraseStep_EraseEligibleEntry_RoundsCapacityUpToBlock()
    {
        var userdata = EraseLayout().Partitions.Single(partition => partition.Name == "userdata");

        var step = MtkScatterPackage.CreateLegacyEraseStep(userdata);

        Assert.Equal("userdata", step.PartitionName);
        Assert.Equal(MtkFlashStepKind.Erase, step.Kind);
        Assert.Equal(0, step.Offset);
        Assert.Equal(65536, step.Length);
        Assert.Null(step.SourcePath);
        Assert.False(step.PadSourceTail);
    }

    [Fact]
    public void CreateLegacyEraseStep_UnalignedCapacity_RoundsUpAndKeepsName()
    {
        var entry = EraseLayout().Partitions.Single(partition => partition.Name == "userdata") with { Size = 65537 };

        Assert.Equal(66048, MtkScatterPackage.CreateLegacyEraseStep(entry).Length);
    }

    [Fact]
    public void GetLegacyPhysicalPartition_StorageFamily_MapsOnlyEmmcUserRegion()
    {
        Assert.Equal((byte)8, MtkScatterPackage.GetLegacyPhysicalPartition(MtkXFlashStorageKind.Emmc));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MtkScatterPackage.GetLegacyPhysicalPartition(MtkXFlashStorageKind.Ufs));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MtkScatterPackage.GetLegacyPhysicalPartition(MtkXFlashStorageKind.Nor));
    }

    [Fact]
    public void CreateXmlStep_ScatterBlockGranularity_RoundsExtentUpAndPadsTail()
    {
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", new byte[600]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        var step = MtkScatterPackage.CreateXmlStep(resolution, 0, verify: true);

        Assert.Equal("boot", step.PartitionName);
        Assert.Equal(MtkFlashStepKind.Raw, step.Kind);
        Assert.Equal(0, step.Offset);
        Assert.Equal(131072, step.Length);
        Assert.Equal(image, step.SourcePath);
        Assert.True(step.Verify);
        Assert.True(step.PadSourceTail);
    }

    [Fact]
    public void CreateXmlStep_LayoutBelowAgentMinimumBlock_IsRejected()
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[600]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img", blockSize: "0x100"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        Assert.Throws<ArgumentOutOfRangeException>(() => MtkScatterPackage.CreateXmlStep(resolution, 0, verify: false));
    }

    [Fact]
    public void CreateXmlEraseStep_ScatterBlockGranularity_RoundsCapacityUp()
    {
        using var dir = new TempDirectory();
        var scatter = dir.Write("scatter.xml", EraseScatter());
        var layout = MtkScatterPackage.Read(scatter).Storages![0];
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));
        var userdata = layout.Partitions.Single(partition => partition.Name == "userdata");

        var step = MtkScatterPackage.CreateXmlEraseStep(resolution, userdata);

        Assert.Equal("userdata", step.PartitionName);
        Assert.Equal(MtkFlashStepKind.Erase, step.Kind);
        Assert.Equal(131072, step.Length);
        Assert.Null(step.SourcePath);
    }

    [Theory]
    [InlineData(MtkXFlashStorageKind.Emmc, "EMMC-USER")]
    [InlineData(MtkXFlashStorageKind.Ufs, "UFS-LUA2")]
    public void GetXmlRegionName_StorageFamily_MatchesReferenceTokens(MtkXFlashStorageKind kind, string expected)
    {
        Assert.Equal(expected, MtkScatterPackage.GetXmlRegionName(kind));
        Assert.Throws<ArgumentOutOfRangeException>(() => MtkScatterPackage.GetXmlRegionName(MtkXFlashStorageKind.Nor));
    }

    [Fact]
    public void CreateXmlStep_SparseContainer_RejectsRawWrite()
    {
        using var dir = new TempDirectory();
        var raw = dir.WriteBytes("raw.img", new byte[4096]);
        using (var sparse = SparseReader.FromRawFile(raw))
        using (var destination = new FileStream(Path.Combine(dir.Path, "boot.img"), FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            SparseWriter.WriteToStream(sparse, destination, includeCrc: true,
                cancellationToken: TestContext.Current.CancellationToken);
        }
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        var error = Assert.Throws<NotSupportedException>(
            () => MtkScatterPackage.CreateXmlStep(resolution, 0, verify: false));

        Assert.Contains("XML download agent cannot write the sparse image", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateXmlStep_NullResolutionOrMissingRow_Throws()
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[600]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        Assert.Throws<ArgumentNullException>(() => MtkScatterPackage.CreateXmlStep(null!, 0, verify: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => MtkScatterPackage.CreateXmlStep(resolution, 4, verify: false));
        Assert.Throws<ArgumentNullException>(() => MtkScatterPackage.CreateLegacyStep(null!, 0, verify: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => MtkScatterPackage.CreateLegacyStep(resolution, 4, verify: false));
    }

    [Fact]
    public void CreateXmlEraseStep_NullResolutionOrPartition_Throws()
    {
        using var dir = new TempDirectory();
        var scatter = dir.Write("scatter.xml", EraseScatter());
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));
        var userdata = resolution.Storage.Partitions.Single(partition => partition.Name == "userdata");

        Assert.Throws<ArgumentNullException>(() => MtkScatterPackage.CreateXmlEraseStep(null!, userdata));
        Assert.Throws<ArgumentNullException>(() => MtkScatterPackage.CreateXmlEraseStep(resolution, null!));
        Assert.Throws<InvalidDataException>(() =>
            MtkScatterPackage.CreateXmlEraseStep(resolution, userdata with { Name = " " }));
        Assert.Throws<InvalidDataException>(() =>
            MtkScatterPackage.CreateXmlEraseStep(resolution, userdata with { Size = 0 }));
    }

    [Theory]
    [InlineData("0x300")]
    [InlineData("0x2000000")]
    public void CreateXmlStep_BlockGranularityOutsideAgentRange_IsRejected(string blockSize)
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[600]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img", blockSize: blockSize));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        Assert.Throws<ArgumentOutOfRangeException>(() => MtkScatterPackage.CreateXmlStep(resolution, 0, verify: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MtkScatterPackage.CreateXmlEraseStep(resolution, resolution.Storage.Partitions[0]));
    }

    [Fact]
    public void CreateStep_SameRowAsTheRawOnlyAgents_KeepsTheUnroundedLength()
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[600]);
        var scatter = dir.Write("scatter.xml", Scatter("boot.img"));
        var resolution = MtkScatterPackage.Resolve(scatter, MtkScatterPackage.Read(scatter));

        var xFlash = MtkScatterPackage.CreateStep(resolution, 0, verify: false);
        var legacy = MtkScatterPackage.CreateLegacyStep(resolution, 0, verify: false);
        var xml = MtkScatterPackage.CreateXmlStep(resolution, 0, verify: false);

        Assert.Equal(600, xFlash.Length);
        Assert.Equal(1024, legacy.Length);
        Assert.Equal(131072, xml.Length);
        Assert.Equal("SYS2", xFlash.PartitionIndex);
        Assert.Equal("boot", legacy.PartitionName);
        Assert.Equal("boot", xml.PartitionName);
        Assert.True(legacy.PadSourceTail);
        Assert.True(xml.PadSourceTail);
    }
}