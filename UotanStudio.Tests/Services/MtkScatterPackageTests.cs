using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkScatterPackageTests
{
    private static string Scatter(string fileName, bool download = true) => $$"""
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
}