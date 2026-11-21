using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Fat;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public class LoongHongFlashPackageTests
{
    private static readonly Guid EfiTypeGuid = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    private static readonly Guid LinuxDataGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid LinuxArm64RootGuid = new("B921B045-1DF0-41C3-AF44-4C6F280D3FAE");

    private static string BuildConfig(string efiSize) => """
    {
      "label": "gpt",
      "partitions": [
        { "name": "efi", "label": "EFI", "size": "{{efiSize}}", "type": "EF00", "fs": "vfat" },
        { "name": "system", "label": "/", "size": "8M", "type": "8300", "fs": "ext4" },
        { "name": "userdata", "label": "/userdata", "size": "0", "type": "8305", "fs": "ext4" }
      ]
    }
    """.Replace("{{efiSize}}", efiSize, StringComparison.Ordinal);

    [Fact]
    public void TryOpen_VendorLayoutWithEfiBootFiles_RecognizesPackage()
    {
        using var temp = new TempDirectory();
        var packagePath = BuildPackage(temp, includeUserData: true);

        var package = LoongHongFlashPackage.TryOpen(packagePath, TestContext.Current.CancellationToken);

        Assert.NotNull(package);
        Assert.Equal(3, package.Partitions.Count);
        Assert.Equal("efi", package.Partitions[0].Name);
        Assert.Equal("EF00", package.Partitions[0].TypeCode);
        Assert.Equal(8L * 1024 * 1024, package.Partitions[0].SizeBytes);
        Assert.Equal(0, package.Partitions[2].SizeBytes);
        Assert.True(package.HasUserDataImage);
        Assert.True(package.EfiBootEntries.ContainsKey("ramdisk.img"));
        Assert.True(package.EfiBootEntries.ContainsKey("vmlinux.efi"));
        Assert.True(package.EstimatedImageSizeBytes > 0);
    }

    [Fact]
    public void TryOpen_ZipWithoutVendorConfig_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "plain.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("no partition table here");
        }

        Assert.Null(LoongHongFlashPackage.TryOpen(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryOpen_NotAZip_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("broken.zip", Encoding.UTF8.GetBytes("this is not a zip archive"));

        Assert.Null(LoongHongFlashPackage.TryOpen(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryOpen_MissingEfiBootFile_Throws()
    {
        using var temp = new TempDirectory();
        var packagePath = BuildPackage(temp, includeUserData: true, includeRamdisk: false);

        var error = Assert.Throws<InvalidDataException>(
            () => LoongHongFlashPackage.TryOpen(packagePath, TestContext.Current.CancellationToken));
        Assert.Contains("ramdisk.img", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_VendorLayout_PublishesGptWithExpectedTypesAndOffsets()
    {
        using var temp = new TempDirectory();
        var packagePath = BuildPackage(temp, includeUserData: true);
        var package = LoongHongFlashPackage.TryOpen(packagePath, TestContext.Current.CancellationToken)!;
        var destination = Path.Combine(temp.Path, "disk.img");

        var result = package.Synthesize(destination, new LoongHongSynthesisOptions(),
            progress: null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(destination));
        Assert.Equal(3, result.PartitionCount);
        Assert.False(result.IncludedUserData);

        using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var blockCount = (ulong)(stream.Length / GptLayout.SectorSize);
        Assert.Equal((long)blockCount * GptLayout.SectorSize, stream.Length);
        var table = GptReader.Read(stream, GptLayout.SectorSize, blockCount,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, table.Partitions.Count);
        Assert.Equal("efi", table.Partitions[0].Name);
        Assert.Equal(EfiTypeGuid, table.Partitions[0].TypeId);
        Assert.Equal(2048UL, table.Partitions[0].FirstLba);
        Assert.Equal("system", table.Partitions[1].Name);
        Assert.Equal(LinuxDataGuid, table.Partitions[1].TypeId);
        Assert.Equal("userdata", table.Partitions[2].Name);
        Assert.Equal(LinuxArm64RootGuid, table.Partitions[2].TypeId);
        Assert.All(table.Partitions, partition => Assert.True(partition.FirstLba >= table.Partitions[0].FirstLba));
    }

    [Fact]
    public void Synthesize_EfiPartition_KeepsVendorVolumeAndAddsBootFiles()
    {
        using var temp = new TempDirectory();
        var packagePath = BuildPackage(temp, includeUserData: true);
        var package = LoongHongFlashPackage.TryOpen(packagePath, TestContext.Current.CancellationToken)!;
        var destination = Path.Combine(temp.Path, "disk.img");

        package.Synthesize(destination, new LoongHongSynthesisOptions(), progress: null,
            TestContext.Current.CancellationToken);

        using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var blockCount = (ulong)(stream.Length / GptLayout.SectorSize);
        var table = GptReader.Read(stream, GptLayout.SectorSize, blockCount,
            TestContext.Current.CancellationToken);
        var efi = table.Partitions[0];
        var efiBytes = (long)(efi.LastLba - efi.FirstLba + 1) * GptLayout.SectorSize;
        stream.Position = (long)efi.FirstLba * GptLayout.SectorSize;

        using var volume = new FatParser(new BoundedReadStream(stream, efiBytes));
        var paths = volume.ListFiles().Select(entry => entry.Path).ToArray();
        Assert.Contains("boot/grub/grub.cfg", paths);
        Assert.Contains("ramdisk.img", paths);
        Assert.Contains("vmlinux.efi", paths);
        Assert.Contains("updater.img", paths);
    }

    [Fact]
    public void Synthesize_EfiPartitionOfVendorSize_KeepsFat32Volume()
    {
        using var temp = new TempDirectory();
        var packagePath = BuildPackage(temp, includeUserData: true, efiSize: "64M");
        var package = LoongHongFlashPackage.TryOpen(packagePath, TestContext.Current.CancellationToken)!;
        var destination = Path.Combine(temp.Path, "disk.img");

        package.Synthesize(destination, new LoongHongSynthesisOptions(), progress: null,
            TestContext.Current.CancellationToken);

        using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var table = GptReader.Read(stream, GptLayout.SectorSize, (ulong)(stream.Length / GptLayout.SectorSize),
            TestContext.Current.CancellationToken);
        var efi = table.Partitions[0];
        stream.Position = efi.ByteOffset;
        using var volume = new FatParser(new BoundedReadStream(stream, efi.ByteLength));
        Assert.Equal("FAT32", volume.Variant);
        Assert.Equal("EFI", volume.VolumeLabel);
    }

    [Fact]
    public void Synthesize_IncludeUserDataExtendsPartitionToTargetCapacity()
    {
        using var temp = new TempDirectory();
        var packagePath = BuildPackage(temp, includeUserData: true);
        var package = LoongHongFlashPackage.TryOpen(packagePath, TestContext.Current.CancellationToken)!;
        var destination = Path.Combine(temp.Path, "disk.img");
        const long capacity = 40L * 1024 * 1024;

        var result = package.Synthesize(destination,
            new LoongHongSynthesisOptions { IncludeUserData = true, CapacityBytes = capacity },
            progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IncludedUserData);
        Assert.Equal(capacity, result.ImageSizeBytes);

        using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var table = GptReader.Read(stream, GptLayout.SectorSize, (ulong)(capacity / GptLayout.SectorSize),
            TestContext.Current.CancellationToken);
        var userData = table.Partitions[^1];
        Assert.Equal("userdata", userData.Name);
        var userDataBytes = (long)(userData.LastLba - userData.FirstLba + 1) * GptLayout.SectorSize;
        Assert.True(userDataBytes > 16L * 1024 * 1024);
    }

    [Fact]
    public void Synthesize_IncludeUserDataWithoutCapacity_Throws()
    {
        using var temp = new TempDirectory();
        var packagePath = BuildPackage(temp, includeUserData: true);
        var package = LoongHongFlashPackage.TryOpen(packagePath, TestContext.Current.CancellationToken)!;
        var destination = Path.Combine(temp.Path, "disk.img");

        Assert.Throws<InvalidDataException>(() => package.Synthesize(destination,
            new LoongHongSynthesisOptions { IncludeUserData = true }, progress: null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Synthesize_Cancelled_RemovesPartialImage()
    {
        using var temp = new TempDirectory();
        var packagePath = BuildPackage(temp, includeUserData: true);
        var package = LoongHongFlashPackage.TryOpen(packagePath, TestContext.Current.CancellationToken)!;
        var destination = Path.Combine(temp.Path, "disk.img");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => package.Synthesize(destination,
            new LoongHongSynthesisOptions(), progress: null, cts.Token));
        Assert.False(File.Exists(destination));
    }

    private static string BuildPackage(TempDirectory temp, bool includeUserData, bool includeRamdisk = true,
        string efiSize = "8M")
    {
        var efiSource = Path.Combine(temp.Path, "efi-source");
        Directory.CreateDirectory(Path.Combine(efiSource, "boot", "grub"));
        File.WriteAllText(Path.Combine(efiSource, "boot", "grub", "grub.cfg"), "set timeout=3");

        var efiImage = Path.Combine(temp.Path, "efi.img");
        FatBuilder.Build(efiSource, efiImage, new FatBuildOptions { VolumeLabel = "EFI", TotalBytes = 8L * 1024 * 1024 });
        var systemImage = Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(Path.Combine(temp.Path, "system-source"));
        Ext4Builder.Build(Path.Combine(temp.Path, "system-source"), systemImage,
            new Ext4Options { TargetSizeBytes = 8L * 1024 * 1024 });
        var userDataImage = Path.Combine(temp.Path, "userdata.img");
        Directory.CreateDirectory(Path.Combine(temp.Path, "userdata-source"));
        Ext4Builder.Build(Path.Combine(temp.Path, "userdata-source"), userDataImage,
            new Ext4Options { TargetSizeBytes = 8L * 1024 * 1024 });

        var packagePath = Path.Combine(temp.Path, "loonghong_flash_tool.zip");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteEntry(archive, "flash_tool/partition_config.json", Encoding.UTF8.GetBytes(BuildConfig(efiSize)));
        WriteEntry(archive, "flash_tool/images/efi.img", File.ReadAllBytes(efiImage));
        WriteEntry(archive, "flash_tool/images/system.img", File.ReadAllBytes(systemImage));
        WriteEntry(archive, "flash_tool/images/vmlinux.efi", Encoding.UTF8.GetBytes("kernel-image"));
        WriteEntry(archive, "flash_tool/images/updater.img", Encoding.UTF8.GetBytes("updater"));
        if (includeRamdisk)
        {
            WriteEntry(archive, "flash_tool/images/ramdisk.img", Encoding.UTF8.GetBytes("initrd-image"));
        }

        if (includeUserData)
        {
            WriteEntry(archive, "flash_tool/images/userdata.img", File.ReadAllBytes(userDataImage));
        }

        return packagePath;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(content);
    }
}