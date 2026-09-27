using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Boot image role tests: init_boot / recovery / boot detection from header version, segment emptiness and
/// partition-name hints, pack-time enforcement of the init_boot empty-kernel rule, and the role reported through
/// the format handler metadata and the directory repack pipeline.
/// <para>Boot 镜像角色测试：依据头部版本、区段空性与分区名提示检测 init_boot / recovery / boot，打包时对 init_boot
/// 空 kernel 规则的强制，以及格式处理器元数据与目录重打包管道报告的角色。</para>
/// </summary>
public class BootImageRoleTests
{
    private static readonly byte[] TestKernel = BuildBytes(1024, factor: 7);
    private static readonly byte[] TestRamdisk = BuildBytes(512, factor: 3);

    private static byte[] BuildBytes(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor + 17) & 0xFF);
        }

        return data;
    }

    private static byte[] PackToArray(BootImagePacker packer)
    {
        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
    }

    private static BootImageHeader V4Header() => new()
    {
        HeaderVersion = BootImageVersion.V4,
        HeaderSize = 1584,
        PageSize = 4096,
        OsVersion = 0x01040000,
        CommandLine = "androidboot.header_version=4",
    };

    private static BootImagePacker NewPacker(BootImageRole? role = null)
    {
        var packer = new BootImagePacker
        {
            Header = V4Header(),
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        };
        packer.Role = role;
        return packer;
    }

    [Theory]
    [InlineData("init_boot")]
    [InlineData("INIT_BOOT")]
    [InlineData("init-boot_a")]
    [InlineData("initboot_b")]
    public void Detect_InitBootHint_MatchesRegardlessOfSlotAndCasing(string partitionName)
    {
        Assert.Equal(BootImageRole.InitBoot, BootImageRoleResolver.Detect(
            BootImageVersion.V4, kernelSize: (uint)TestKernel.Length, ramdiskSize: (uint)TestRamdisk.Length, partitionName));
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("recovery_b")]
    public void Detect_RecoveryHint_ReturnsRecovery(string partitionName)
    {
        Assert.Equal(BootImageRole.Recovery, BootImageRoleResolver.Detect(
            BootImageVersion.V2, kernelSize: 1024, ramdiskSize: 512, partitionName));
    }

    [Fact]
    public void Detect_V4EmptyKernelWithRamdiskWithoutHint_ReturnsInitBoot()
    {
        Assert.Equal(BootImageRole.InitBoot, BootImageRoleResolver.Detect(
            BootImageVersion.V4, kernelSize: 0, ramdiskSize: 512));
    }

    [Fact]
    public void Detect_V4WithKernelWithoutHint_ReturnsBoot()
    {
        Assert.Equal(BootImageRole.Boot, BootImageRoleResolver.Detect(
            BootImageVersion.V4, kernelSize: 1024, ramdiskSize: 512));
    }

    [Fact]
    public void Detect_BootHintOverridesEmptyKernelHeuristic_ReturnsBoot()
    {
        Assert.Equal(BootImageRole.Boot, BootImageRoleResolver.Detect(
            BootImageVersion.V4, kernelSize: 0, ramdiskSize: 512, partitionName: "boot_a"));
    }

    [Fact]
    public void Detect_UnrecognizedHint_FallsBackToContentHeuristic()
    {
        Assert.Equal(BootImageRole.InitBoot, BootImageRoleResolver.Detect(
            BootImageVersion.V4, kernelSize: 0, ramdiskSize: 512, partitionName: "vendor-ramdisk-repair"));
    }

    [Fact]
    public void Pack_InitBootRoleWithNonEmptyKernel_ThrowsArgumentExceptionWithPreciseMessage()
    {
        var packer = NewPacker(BootImageRole.InitBoot);

        var exception = Assert.Throws<ArgumentException>(() => PackToArray(packer));
        Assert.Contains("empty kernel segment", exception.Message, StringComparison.Ordinal);
        Assert.Equal(nameof(BootImagePacker.Kernel), exception.ParamName);
    }

    [Fact]
    public void Pack_InitBootRoleWithVersionThree_ThrowsArgumentException()
    {
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V3,
                HeaderSize = 1580,
                PageSize = 4096,
            },
            Ramdisk = TestRamdisk,
        };
        packer.Role = BootImageRole.InitBoot;

        var exception = Assert.Throws<ArgumentException>(() => PackToArray(packer));
        Assert.Contains("header version 4", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_InitBootRoleWithEmptyKernel_PacksAndReparsesAsInitBoot()
    {
        var packer = new BootImagePacker
        {
            Header = V4Header(),
            Ramdisk = TestRamdisk,
        };
        packer.Role = BootImageRole.InitBoot;

        var image = PackToArray(packer);
        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(0u, parser.Header.KernelSize);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(BootImageRole.InitBoot, parser.DetectRole());
        Assert.Equal(BootImageRole.InitBoot, parser.DetectRole("init_boot"));
    }

    [Fact]
    public void Pack_RecoveryRoleWithVersionFour_ThrowsArgumentException()
    {
        var packer = NewPacker(BootImageRole.Recovery);

        var exception = Assert.Throws<ArgumentException>(() => PackToArray(packer));
        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pack_BootRoleLeavesVersionFourEmptyKernelImageUnrestricted()
    {
        var packer = new BootImagePacker
        {
            Header = V4Header(),
            Ramdisk = TestRamdisk,
        };
        packer.Role = BootImageRole.Boot;

        var image = PackToArray(packer);

        Assert.StartsWith("ANDROID!", System.Text.Encoding.ASCII.GetString(image, 0, 8));
    }

    [Fact]
    public async Task ReadMetadata_InitBootNamedImage_ReportsInitBootRole()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-role-" + Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(directory, "init_boot.img");
        Directory.CreateDirectory(directory);
        try
        {
            var packer = new BootImagePacker { Header = V4Header(), Ramdisk = TestRamdisk };
            packer.Role = BootImageRole.InitBoot;
            File.WriteAllBytes(imagePath, PackToArray(packer));

            var result = await new BootImageFormatHandler().ReadMetadataAsync(
                imagePath, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Summary);
            Assert.Equal("InitBoot", result.Metadata["Role"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackDirectory_InitBootHintWithKernelFile_FailsWithEmptyKernelMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-role-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "init_boot.img");
        var extractPath = Path.Combine(directory, "extract");
        var outputPath = Path.Combine(directory, "rebuilt.img");
        try
        {
            File.WriteAllBytes(imagePath, PackToArray(NewPacker()));

            var handler = new BootImageFormatHandler();
            Assert.True((await handler.UnpackAsync(imagePath, extractPath,
                cancellationToken: TestContext.Current.CancellationToken)).Success);

            var result = await handler.PackAsync(extractPath, outputPath, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains("empty kernel segment", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackDirectory_InitBootHintWithEmptyKernel_RepacksByteIdentically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-role-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "init_boot.img");
        var extractPath = Path.Combine(directory, "extract");
        var outputPath = Path.Combine(directory, "rebuilt.img");
        try
        {
            var packer = new BootImagePacker { Header = V4Header(), Ramdisk = TestRamdisk };
            packer.Role = BootImageRole.InitBoot;
            var source = PackToArray(packer);
            File.WriteAllBytes(imagePath, source);

            var handler = new BootImageFormatHandler();
            Assert.True((await handler.UnpackAsync(imagePath, extractPath,
                cancellationToken: TestContext.Current.CancellationToken)).Success);
            Assert.True((await handler.PackAsync(extractPath, outputPath, TestContext.Current.CancellationToken)).Success);

            Assert.Equal(source, File.ReadAllBytes(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}