using System.Buffers.Binary;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PackingOptionPropagationTests
{
    [Fact]
    public async Task ProjectSettings_DefaultSaveAndReopen_PreservesInheritedExt4Policy()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "default.uotanfw");
        var settings = new ProjectSettingViewModel();

        Assert.True(await SolutionPersistenceService.SaveFirmwareProjectAsync(path, new FirmwareProjectSnapshot
        {
            ProjectName = "default",
            Settings = settings.ToDto()
        }, TestContext.Current.CancellationToken));
        var reopened = await SolutionPersistenceService.LoadFirmwareProjectAsync(path, TestContext.Current.CancellationToken);
        var restored = new ProjectSettingViewModel();
        restored.ApplyFromDto(Assert.IsType<ProjectSettingsDto>(reopened!.Settings));

        Assert.False(restored.HasExt4PolicyOverride);
        Assert.Equal(Ext4CapacityPolicy.Inherit, restored.Ext4CapacityPolicy);
        Assert.Null(restored.ToDto().Ext4Compress);
        Assert.Null(restored.ToDto().Ext4CapacityPolicy);
    }

    [Fact]
    public void ProjectSettings_LegacyExt4Compress_MigratesToMinimizeIntent()
    {
        var settings = new ProjectSettingViewModel();

        settings.ApplyFromDto(new ProjectSettingsDto { ImageType = "EXT4", Ext4Compress = true });

        Assert.Equal(Ext4CapacityPolicy.Minimize, settings.Ext4CapacityPolicy);
        Assert.True(settings.HasExt4PolicyOverride);
        Assert.Equal(Ext4CapacityPolicy.Minimize, settings.ToDto().Ext4CapacityPolicy);
    }

    [Fact]
    public void BuildImage_Ext4ExplicitCapacityAndReadOnly_ReachBuilder()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("source/build.prop", "ro.product.name=uotan\n"u8.ToArray());
        var output = Path.Combine(temp.Path, "system.img");
        const long targetSize = 16 * 1024 * 1024;
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "EXT4",
            Ext4Compress = false,
            Ext4CapacityPolicy = Ext4CapacityPolicy.Explicit,
            Ext4TargetSizeBytes = targetSize,
            Ext4ReadOnly = true,
            Ext4BlockSharingMode = nameof(Ext4BlockSharingMode.Independent)
        });

        var result = PackingService.BuildImage(Path.Combine(temp.Path, "source"), output, settings);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(targetSize, new FileInfo(output).Length);
        using var image = File.OpenRead(output);
        image.Position = 1024 + 0x64;
        Span<byte> feature = stackalloc byte[4];
        image.ReadExactly(feature);
        Assert.NotEqual(0U, BinaryPrimitives.ReadUInt32LittleEndian(feature) & 0x1000U);
    }

    [Fact]
    public void BuildImage_Ext4Minimize_ReachesBuilderAndPublishesCompactOutput()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("source/file", "content"u8.ToArray());
        var output = Path.Combine(temp.Path, "system.img");
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "EXT4",
            Ext4Compress = true
        });

        var result = PackingService.BuildImage(Path.Combine(temp.Path, "source"), output, settings);

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(output));
        using var parser = new UotanStudio.Firmware.FileSystem.Ext4.Ext4Parser(output);
        Assert.Equal(new FileInfo(output).Length, parser.PartitionSize);
    }

    [Theory]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    [InlineData("Android Sparse Image (simg)")]
    public async Task BuildImage_PreservedExtractionFromAnotherFormat_RebuildsAsExt4(string sourceFormat)
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var image = Path.Combine(temp.Path, "source.img");
        var extracted = Path.Combine(temp.Path, "extracted");
        var output = Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "build.prop"), "ro.product.name=uotan\n");

        switch (sourceFormat)
        {
            case "EROFS":
                var erofs = new ErofsFormatHandler();
                Assert.True((await erofs.PackAsync(source, image, TestContext.Current.CancellationToken)).Success);
                Assert.True((await erofs.UnpackAsync(image, extracted,
                    cancellationToken: TestContext.Current.CancellationToken)).Success);
                break;
            case "F2FS":
                var f2fs = new F2fsFormatHandler();
                Assert.True((await f2fs.PackAsync(source, image, TestContext.Current.CancellationToken)).Success);
                Assert.True((await f2fs.UnpackAsync(image, extracted,
                    cancellationToken: TestContext.Current.CancellationToken)).Success);
                break;
            default:
                var raw = Path.Combine(temp.Path, "raw.img");
                Ext4Builder.Build(source, raw, new Ext4Options
                {
                    CancellationToken = TestContext.Current.CancellationToken
                });
                SparseImageConverter.ConvertRawToSparse(raw, image);
                Assert.True((await new SparseFormatHandler().UnpackAsync(image, extracted,
                    cancellationToken: TestContext.Current.CancellationToken)).Success);
                break;
        }

        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "EXT4",
            Ext4CapacityPolicy = Ext4CapacityPolicy.Minimize,
        });

        var result = PackingService.BuildImage(extracted, output, settings);

        Assert.True(result.Success, result.Summary);
        using var built = File.OpenRead(output);
        built.Position = 1024 + 0x38;
        Span<byte> magic = stackalloc byte[2];
        built.ReadExactly(magic);
        Assert.Equal(0xEF53, BinaryPrimitives.ReadUInt16LittleEndian(magic));
    }

    [Fact]
    public void ProjectSettings_InvalidExt4Enums_AreRejected()
    {
        var settings = new ProjectSettingViewModel();

        Assert.Throws<InvalidDataException>(() => settings.ApplyFromDto(new ProjectSettingsDto
        {
            Ext4CapacityPolicy = (Ext4CapacityPolicy)999,
        }));
        Assert.Throws<InvalidDataException>(() => settings.ApplyFromDto(new ProjectSettingsDto
        {
            Ext4BlockSharingMode = "999",
        }));
    }

    [Theory]
    [InlineData("CapacityPolicy", "999")]
    [InlineData("BlockSharingMode", "999")]
    public async Task Pack_PreservedExt4WithInvalidOverride_RejectsAndPreservesOutput(string key, string value)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var image = Path.Combine(temp.Path, "source.img");
        var extracted = Path.Combine(temp.Path, "extracted");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file"), "content");
        var handler = new UotanStudio.Firmware.FileSystem.Ext4.Ext4FormatHandler();
        Assert.True((await handler.PackAsync(source, image, TestContext.Current.CancellationToken)).Success);
        Assert.True((await handler.UnpackAsync(image, extracted,
            cancellationToken: TestContext.Current.CancellationToken)).Success);
        var existing = "existing"u8.ToArray();
        var output = temp.WriteBytes("output.img", existing);

        using (RepackMetadata.UsePackingOverrides(new Dictionary<string, string> { [key] = value }))
        {
            var result = await handler.PackAsync(extracted, output, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
        }

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task ProjectSettings_ErofsFeatureOverrides_SaveAndReopen_RestoresOverrideIntent()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "features.uotanfw");
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "EROFS",
            ErofsFeatureOverrides = "!big_pcluster"
        });
        Assert.True(settings.HasErofsFeatureOverride);

        Assert.True(await SolutionPersistenceService.SaveFirmwareProjectAsync(path, new FirmwareProjectSnapshot
        {
            ProjectName = "features",
            Settings = settings.ToDto()
        }, TestContext.Current.CancellationToken));
        var reopened = await SolutionPersistenceService.LoadFirmwareProjectAsync(path, TestContext.Current.CancellationToken);
        var restored = new ProjectSettingViewModel();
        restored.ApplyFromDto(Assert.IsType<ProjectSettingsDto>(reopened!.Settings));

        Assert.Equal("!big_pcluster", restored.ErofsFeatureOverrides);
        Assert.True(restored.HasErofsFeatureOverride);
        Assert.Equal("!big_pcluster", restored.ToDto().ErofsFeatureOverrides);
    }

    [Fact]
    public async Task Pack_PreservedErofsWithFeatureClear_RebuildsWithoutBigPclusterBit()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var image = Path.Combine(temp.Path, "product.img");
        var extracted = Path.Combine(temp.Path, "product");
        var output = Path.Combine(temp.Path, "rebuilt.img");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.txt"), new string('a', 96 * 1024));
        new ErofsBuilder().Build(source, image, new ErofsOptions
        {
            LegacyCompatibility = false,
            CancellationToken = TestContext.Current.CancellationToken
        });
        Assert.Equal(0x3U, ReadErofsIncompat(image));
        Assert.True((await new ErofsFormatHandler().UnpackAsync(image, extracted,
            cancellationToken: TestContext.Current.CancellationToken)).Success);
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "EROFS",
            ErofsFeatureOverrides = "!big_pcluster"
        });

        var cleared = PackingService.BuildImage(extracted, output, settings);
        Assert.True(cleared.Success, cleared.Summary);
        Assert.Equal(0x1U, ReadErofsIncompat(output));
        Assert.True((ReadErofsIncompat(output) & 0x2U) == 0);

        var inheritedSettings = new ProjectSettingViewModel();
        inheritedSettings.ApplyFromDto(new ProjectSettingsDto { ImageType = "EROFS" });
        var inheritedPath = Path.Combine(temp.Path, "inherited.img");
        var inherited = PackingService.BuildImage(extracted, inheritedPath, inheritedSettings);
        Assert.True(inherited.Success, inherited.Summary);
        Assert.Equal(0x3U, ReadErofsIncompat(inheritedPath));
    }

    [Fact]
    public async Task Pack_PreservedErofsWithDotOmittedDisabledKey_ClearsTheInodeAdviceDimension()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var image = Path.Combine(temp.Path, "product.img");
        var extracted = Path.Combine(temp.Path, "product");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.txt"), new string('c', 48 * 1024));
        new ErofsBuilder().Build(source, image, new ErofsOptions
        {
            LegacyCompatibility = false,
            CancellationToken = TestContext.Current.CancellationToken
        });
        Assert.True((await new ErofsFormatHandler().UnpackAsync(image, extracted,
            cancellationToken: TestContext.Current.CancellationToken)).Success);
        var handler = new ErofsFormatHandler();
        var inheritedPath = Path.Combine(temp.Path, "inherited.img");
        Assert.True((await handler.PackAsync(extracted, inheritedPath,
            TestContext.Current.CancellationToken)).Success);

        using (RepackMetadata.UsePackingOverrides(new Dictionary<string, string> { ["ErofsDotOmitted"] = "Disabled" }))
        {
            var clearedPath = Path.Combine(temp.Path, "cleared.img");
            Assert.True((await handler.PackAsync(extracted, clearedPath,
                TestContext.Current.CancellationToken)).Success);
            using var clearedParser = new ErofsParser(clearedPath);
            Assert.Equal(ErofsTriStateFeature.Disabled, clearedParser.ReadRebuildOptions().DotOmitted);
        }

        using var inheritedParser = new ErofsParser(inheritedPath);
        Assert.Equal(ErofsTriStateFeature.Enabled, inheritedParser.ReadRebuildOptions().DotOmitted);
    }

    [Fact]
    public async Task Pack_PreservedLegacyErofsWithDotOmittedEnabledKey_FailsAndKeepsExistingOutput()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var image = Path.Combine(temp.Path, "product.img");
        var extracted = Path.Combine(temp.Path, "product");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.txt"), new string('d', 48 * 1024));
        new ErofsBuilder().Build(source, image, new ErofsOptions
        {
            LegacyCompatibility = true,
            CancellationToken = TestContext.Current.CancellationToken
        });
        Assert.True((await new ErofsFormatHandler().UnpackAsync(image, extracted,
            cancellationToken: TestContext.Current.CancellationToken)).Success);
        var existing = "existing"u8.ToArray();
        var output = temp.WriteBytes("out.img", existing);

        using (RepackMetadata.UsePackingOverrides(new Dictionary<string, string> { ["ErofsDotOmitted"] = "Enabled" }))
        {
            var result = await new ErofsFormatHandler().PackAsync(extracted, output,
                TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains("dot-omitted", result.Summary, StringComparison.Ordinal);
        }

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task Pack_PreservedErofsWithUnknownFeatureToken_FailsAndKeepsExistingOutput()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var image = Path.Combine(temp.Path, "product.img");
        var extracted = Path.Combine(temp.Path, "product");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.txt"), new string('b', 64 * 1024));
        new ErofsBuilder().Build(source, image, new ErofsOptions
        {
            LegacyCompatibility = false,
            CancellationToken = TestContext.Current.CancellationToken
        });
        Assert.True((await new ErofsFormatHandler().UnpackAsync(image, extracted,
            cancellationToken: TestContext.Current.CancellationToken)).Success);
        var existing = "existing"u8.ToArray();
        var output = temp.WriteBytes("out.img", existing);
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "EROFS",
            ErofsFeatureOverrides = "not_a_feature"
        });

        var result = PackingService.BuildImage(extracted, output, settings);

        Assert.False(result.Success);
        Assert.Contains("not_a_feature", result.Summary, StringComparison.Ordinal);
        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    private static uint ReadErofsIncompat(string image)
    {
        using var stream = File.OpenRead(image);
        stream.Position = 1024 + 80;
        Span<byte> word = stackalloc byte[4];
        stream.ReadExactly(word);
        return BinaryPrimitives.ReadUInt32LittleEndian(word);
    }
}