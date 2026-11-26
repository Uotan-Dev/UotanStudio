using UotanStudio.Services;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public class FileAssociationServiceTests
{
    [Fact]
    public void BuildWindowsOpenCommand_QuotesExecutableAndPlaceholder()
    {
        var command = FileAssociationService.BuildWindowsOpenCommand(@"C:\Tools\UotanStudio.Desktop.exe");

        Assert.Equal("\"C:\\Tools\\UotanStudio.Desktop.exe\" \"%1\"", command);
    }

    public static TheoryData<string, string> FormatIconCases => new()
    {
        { ".apk", "apk.ico" },
        { ".arsc", "arsc.ico" },
        { ".dex", "dex.ico" },
        { ".hap", "hap.ico" },
        { ".logcat", "log.ico" },
        { ".prop", "prop.ico" },
        { ".qcn", "qcn.ico" },
        { ".rc", "rc.ico" },
        { ".img", "img.ico" },
        { ".f2fs", "img.ico" },
    };

    [Theory]
    [MemberData(nameof(FormatIconCases))]
    public void IconFileFor_FormatExtension_ReturnsMatchingIcon(string extension, string expected)
    {
        Assert.Equal(expected, FileAssociationService.IconFileFor(extension));
    }

    [Fact]
    public void IconFileFor_UnknownExtension_FallsBackToImageIcon()
    {
        Assert.Equal("img.ico", FileAssociationService.IconFileFor(".unknown-format"));
    }

    [Fact]
    public void BuildSpecs_AssociatedFormats_UseFormatSpecificIcons()
    {
        var specs = FileAssociationService.BuildSpecs([".apk", ".hap", ".qcn", ".xyz"]);

        Assert.Contains(specs, spec => spec.Extension == ".apk" && spec.IconFile == "apk.ico");
        Assert.Contains(specs, spec => spec.Extension == ".hap" && spec.IconFile == "hap.ico");
        Assert.Contains(specs, spec => spec.Extension == ".qcn" && spec.IconFile == "qcn.ico");
        Assert.Contains(specs, spec => spec.Extension == ".xyz" && spec.IconFile == "img.ico");
    }

    [Fact]
    public void Resolve_FormatExtension_UsesBundledFormatIcon()
    {
        Assert.Equal(AppIconKind.FileApk, ExplorerIconCatalog.Resolve("app.apk", false).Kind);
        Assert.Equal(AppIconKind.FileImg, ExplorerIconCatalog.Resolve("system.img", false).Kind);
        Assert.Equal(AppIconKind.FileLog, ExplorerIconCatalog.Resolve("kernel.logcat", false).Kind);
        Assert.Equal("apk", ExplorerIconCatalog.FormatIconName(".apk"));
        Assert.Null(ExplorerIconCatalog.FormatIconName(".uotan"));
    }
}