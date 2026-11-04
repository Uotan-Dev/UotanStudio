using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Acceptance checks of the flash page package layouts against real firmware folders, gated by
/// <see cref="CorpusVariable"/> naming a directory that holds the unpacked packages.
/// <para>用真实固件目录验收烧录页的烧录包布局识别；由 <see cref="CorpusVariable"/> 指向存放已解包
/// 固件的目录来启用。</para>
/// </summary>
public sealed class FlashPackageLayoutCorpusTests
{
    private const string CorpusVariable = "UOTAN_FLASH_PACKAGE_CORPUS";

    [Fact]
    public void BianbuK1Folder_SpacemiTLayout_MapsEveryUniversalPartitionImage()
    {
        var root = RequirePackage("Bianbu-Minimal-K1");

        Assert.True(FlashSpacemiTPackage.HasStructure(root));
        var contents = FlashSpacemiTPackage.Read(root)!;

        Assert.Equal("gpt", contents.Partitions[0].TableFormat);
        Assert.Equal(
            ["bootinfo", "fsbl", "env", "opensbi", "uboot", "bootfs", "rootfs"],
            contents.Partitions.Select(entry => entry.Partition));
        Assert.All(contents.Partitions, entry => Assert.True(File.Exists(entry.FilePath), entry.Partition));
        Assert.True(contents.Partitions.Single(entry => entry.Partition == "rootfs").SizeBytes < 0);
        Assert.Equal(0, contents.Partitions[0].OffsetBytes);
    }

    [Fact]
    public void BianbuK3Folder_SpacemiTLayout_MapsEveryUniversalPartitionImage()
    {
        var root = RequirePackage("Bianbu-LXQt-K3");

        var contents = FlashSpacemiTPackage.Read(root)!;

        Assert.Equal(
            ["env", "bootinfo", "fsbl", "esos", "opensbi", "uboot", "ESP", "bootfs", "rootfs"],
            contents.Partitions.Select(entry => entry.Partition));
        Assert.All(contents.Partitions, entry => Assert.True(File.Exists(entry.FilePath), entry.Partition));
    }

    [Fact]
    public void OpenHarmonyRv2Folder_SpacemiTLayout_MapsOpenHarmonyImages()
    {
        var root = RequirePackage("orangepirv2");

        var contents = FlashSpacemiTPackage.Read(root)!;

        var names = contents.Partitions.Select(entry => entry.Partition).ToArray();
        Assert.Equal(
            ["bootinfo", "fsbl", "env", "opensbi", "uboot", "boot", "ramdisk", "misc", "updater",
             "sys_prod", "chip_prod", "system", "vendor", "userdata"],
            names);
        Assert.All(contents.Partitions.Where(entry => entry.Partition != "misc"),
            entry => Assert.True(File.Exists(entry.FilePath), entry.Partition));
        Assert.Null(contents.Partitions.Single(entry => entry.Partition == "misc").FilePath);
        Assert.True(contents.Partitions.Single(entry => entry.Partition == "userdata").SizeBytes < 0);
    }

    [Fact]
    public void FerrariFolder_QualcommFlatLayout_MapsRawprogramImagesAndMbnProgrammer()
    {
        var root = RequirePackage("ferrari_images");

        var package = FlashBinPackage.Read(root);

        var partitions = package.Images.Select(entry => entry.Partition).ToArray();
        Assert.Contains("sbl1", partitions);
        Assert.Contains("sbl1bak", partitions);
        Assert.Contains("rpm", partitions);
        Assert.Contains("tz", partitions);
        Assert.Contains("boot", partitions);
        Assert.Contains("recovery", partitions);
        Assert.Contains("system", partitions);
        Assert.DoesNotContain(partitions, name => name is "PrimaryGPT" or "BackupGPT");
        Assert.All(package.Images, entry => Assert.True(File.Exists(entry.FilePath), entry.Partition));
        Assert.EndsWith("prog_emmc_firehose_8936.mbn", Path.GetFileName(package.Programmer),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnePlusFlatFolder_QualcommFlatLayout_MapsMultiLunImagesAndDualProgrammers()
    {
        var root = RequirePackage("LE2110");

        var package = FlashBinPackage.Read(root);

        var partitions = package.Images.Select(entry => entry.Partition).ToArray();
        Assert.True(partitions.Length > 20, $"expected a full flat build, got {partitions.Length} rows");
        Assert.Contains("param", partitions);
        Assert.Contains("persist", partitions);
        Assert.Contains("frp", partitions);
        Assert.DoesNotContain(partitions, name => name is "PrimaryGPT" or "BackupGPT");
        Assert.All(package.Images, entry => Assert.True(File.Exists(entry.FilePath), entry.Partition));
        Assert.True(package.Images.Select(entry => entry.Program.PhysicalPartitionNumber).Distinct().Count() > 1,
            "expected images across several LUNs");
        Assert.Null(package.Programmer);
        Assert.Equal(2, package.Programmers.Count);
    }

    [Fact]
    public async Task RealmeOfpFolder_SingleArchive_ListsScatterDrivenImageEntries()
    {
        var root = RequirePackage("RMX3121");

        var archives = Directory.EnumerateFiles(root, "*.ofp", SearchOption.TopDirectoryOnly).ToArray();
        var archive = Assert.Single(archives);

        var entries = await FlashOppoContainerService.ListAsync(archive, TestContext.Current.CancellationToken);

        var names = entries.Select(entry => entry.Name).ToArray();
        Assert.True(names.Length > 20, $"expected scatter-driven rows, got {names.Length}");
        Assert.Contains("boot", names);
        Assert.Contains("recovery", names);
        Assert.Contains("preloader", names);
        Assert.Contains("vbmeta", names);
        Assert.Contains("md1img", names);
        Assert.DoesNotContain(names, name => name.Equals("Projectconfig", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.StartsWith("all-in-one-signature", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(names, names.Distinct().ToArray());
    }

    /// <summary>
    /// Returns the corpus package folder whose name starts with the prefix, or skips the test when the corpus
    /// or that package is unavailable.
    /// <para>返回名称以指定前缀开头的语料包目录；语料或该包不可用时跳过测试。</para>
    /// </summary>
    /// <param name="prefix">Package folder name prefix. <para>包目录名前缀。</para></param>
    private static string RequirePackage(string prefix)
    {
        var directory = Environment.GetEnvironmentVariable(CorpusVariable);
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
            $"Set {CorpusVariable} to a directory of real firmware packages to run the flash page layout acceptance.");
        var matches = Directory.EnumerateDirectories(directory!, $"{prefix}*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.SkipUnless(
            matches.Length > 0,
            $"Set {CorpusVariable} to a directory containing the '{prefix}' package folder to run this acceptance.");
        return matches[0];
    }
}