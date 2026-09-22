using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PackingServiceF2fsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-packing-f2fs-" + Guid.NewGuid().ToString("N"));

    public PackingServiceF2fsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ProjectSettings_F2fs_RoundTripsWithoutFallingBackToErofs()
    {
        var settings = new ProjectSettingViewModel();

        settings.ApplyFromDto(new ProjectSettingsDto { ImageType = "F2FS", ImageFormat = "RAW" });
        var saved = settings.ToDto();

        Assert.True(settings.TypeF2FS);
        Assert.False(settings.TypeEXT4);
        Assert.False(settings.TypeEROFS);
        Assert.Equal("F2FS", saved.ImageType);
    }

    [Fact]
    public void BuildImage_F2fsSelection_ProducesParseableImage()
    {
        var source = Path.Combine(_root, "source");
        var output = Path.Combine(_root, "system.img");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "build.prop"), "ro.product=test\n");
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto { ImageType = "F2FS", ImageFormat = "RAW" });

        var result = PackingService.BuildImage(source, output, settings);

        Assert.True(result.Success, result.Summary);
        using var parser = new F2fsParser(output);
        Assert.Contains(parser.ListFiles(), entry => entry.Path == "build.prop");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}