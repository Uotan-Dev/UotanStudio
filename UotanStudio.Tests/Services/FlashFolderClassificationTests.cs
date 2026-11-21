using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public class FlashFolderClassificationTests
{
    [Fact]
    public void ClassifyFolder_XiaomiStylePackageWithScatterUnderImages_RecognizesMtkScatter()
    {
        using var temp = new TempDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        var scatter = Path.Combine(images, "MT6785_Android_scatter.txt");
        File.WriteAllText(scatter, ScatterText);

        var (kind, document) = FlashViewModel.ClassifyFolder(temp.Path);

        Assert.Equal(FlashViewModel.FlashFolderKind.MtkScatter, kind);
        var resolved = Assert.IsType<string>(document);
        Assert.Equal(Path.GetFullPath(scatter), Path.GetFullPath(resolved));
    }

    [Fact]
    public void ClassifyFolder_UpperInvariantImagesDirectory_RecognizesMtkScatter()
    {
        using var temp = new TempDirectory();
        var images = Path.Combine(temp.Path, "IMAGES");
        Directory.CreateDirectory(images);
        var scatter = Path.Combine(images, "MT6785_Android_scatter.xml");
        File.WriteAllText(scatter, ScatterText);

        var (kind, document) = FlashViewModel.ClassifyFolder(temp.Path);

        Assert.Equal(FlashViewModel.FlashFolderKind.MtkScatter, kind);
        var resolved = Assert.IsType<string>(document);
        Assert.Equal(Path.GetFullPath(scatter), Path.GetFullPath(resolved));
    }

    [Fact]
    public void ClassifyFolder_ScatterAtPackageRoot_StillWinsOverImagesChild()
    {
        using var temp = new TempDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        File.WriteAllText(Path.Combine(images, "MT6785_Android_scatter.txt"), ScatterText);
        var rootScatter = Path.Combine(temp.Path, "MT6785_Android_scatter.txt");
        File.WriteAllText(rootScatter, ScatterText);

        var (kind, document) = FlashViewModel.ClassifyFolder(temp.Path);

        Assert.Equal(FlashViewModel.FlashFolderKind.MtkScatter, kind);
        var resolved = Assert.IsType<string>(document);
        Assert.Equal(Path.GetFullPath(rootScatter), Path.GetFullPath(resolved));
    }

    [Fact]
    public void ClassifyFolder_ScatterNamedFileWithoutMtkPlatform_FallsBackToImages()
    {
        using var temp = new TempDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        File.WriteAllText(Path.Combine(images, "MT6785_Android_scatter.txt"), "not a scatter document");

        var (kind, document) = FlashViewModel.ClassifyFolder(temp.Path);

        Assert.Equal(FlashViewModel.FlashFolderKind.Images, kind);
        Assert.Null(document);
    }

    [Fact]
    public void ClassifyFolder_FolderWithoutScatterAnywhere_FallsBackToImages()
    {
        using var temp = new TempDirectory();
        var images = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(images);
        File.WriteAllText(Path.Combine(images, "boot.img"), "x");

        var (kind, document) = FlashViewModel.ClassifyFolder(temp.Path);

        Assert.Equal(FlashViewModel.FlashFolderKind.Images, kind);
        Assert.Null(document);
    }

    private const string ScatterText = """
        - general: MTK_PLATFORM_CFG
          info:
          - config_version: V2.1.0
            platform: MT6785
            project: dandelion
        - storage_type: EMMC
          description:
          - general: MTK_STORAGE_CFG
            info:
            - storage: EMMC
              boot_channel: MSDC_0
              block_size: 0x20000
          - partition_index: SYS0
            partition_name: preloader
            file_name: preloader_dandelion.bin
            is_download: true
            type: SV5_BL_BIN
            linear_start_addr: 0x0
            physical_start_addr: 0x0
            partition_size: 0x80000
            region: EMMC_BOOT1_BOOT2
            storage: HW_STORAGE_EMMC
            boundary_check: true
            is_reserved: false
            operation_type: BOOTLOADERS
            empty_boot_needed: false
        """;
}