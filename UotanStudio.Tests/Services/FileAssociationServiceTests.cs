using UotanStudio.Services;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Editors;
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
        { ".ruu", "img.ico" },
    };

    [Theory]
    [MemberData(nameof(FormatIconCases))]
    public void IconFileFor_FormatExtension_ReturnsMatchingIcon(string extension, string expected)
    {
        Assert.Equal(expected, FileAssociationService.IconFileFor(extension));
    }

    [Fact]
    public void IconFileFor_UnknownExtension_FallsBackToFileIcon()
    {
        Assert.Equal("file.ico", FileAssociationService.IconFileFor(".unknown-format"));
    }

    [Fact]
    public void BuildSpecs_AssociatedFormats_UseFormatSpecificIcons()
    {
        var specs = FileAssociationService.BuildSpecs([".apk", ".hap", ".qcn", ".ruu", ".xyz"]);

        Assert.Contains(specs, spec => spec.Extension == ".apk" && spec.IconFile == "apk.ico");
        Assert.Contains(specs, spec => spec.Extension == ".hap" && spec.IconFile == "hap.ico");
        Assert.Contains(specs, spec => spec.Extension == ".qcn" && spec.IconFile == "qcn.ico");
        Assert.Contains(specs, spec => spec.Extension == ".ruu" && spec.IconFile == "img.ico");
        Assert.Contains(specs, spec => spec.Extension == ".xyz" && spec.IconFile == "file.ico");
    }

    [Fact]
    public void Resolve_AnyFileExtension_UsesSingleFileIcon()
    {
        Assert.Equal(AppIconKind.File, ExplorerIconCatalog.Resolve("app.apk", false).Kind);
        Assert.Equal(AppIconKind.File, ExplorerIconCatalog.Resolve("system.img", false).Kind);
        Assert.Equal(AppIconKind.File, ExplorerIconCatalog.Resolve("kernel.logcat", false).Kind);
        Assert.Equal("apk", ExplorerIconCatalog.FormatIconName(".apk"));
        Assert.Null(ExplorerIconCatalog.FormatIconName(".uotan"));
    }

    [Fact]
    public void FileManagerEntry_UsesSingleFileIconCatalog()
    {
        Assert.Equal(AppIconKind.File, new FileManagerEntryViewModel("update.apk", "/update.apk", 1, FileEntryKind.File).IconKind);
        Assert.Equal(AppIconKind.File, new FileManagerEntryViewModel("system.img", "/system.img", 1, FileEntryKind.File).IconKind);
        Assert.Equal(AppIconKind.File, new FileManagerEntryViewModel("kernel.logcat", "/kernel.logcat", 1, FileEntryKind.File).IconKind);
        Assert.Equal(AppIconKind.File, new FileManagerEntryViewModel("unknown.zzz", "/unknown.zzz", 1, FileEntryKind.File).IconKind);
        Assert.Equal(AppIconKind.Folder, new FileManagerEntryViewModel("system", "/system", 0, FileEntryKind.Directory).IconKind);
    }

    [Fact]
    public void BuildSpecs_BuiltInFormats_UseDedicatedIconsOnAllPlatforms()
    {
        var specs = FileAssociationService.BuildSpecs(null);

        Assert.Contains(specs, spec => spec.Extension == ".uotan" && FileAssociationService.LinuxIconName(spec) == "uotan");
        Assert.Contains(specs, spec => spec.Extension == ".uotanfw" && FileAssociationService.LinuxIconName(spec) == "uotanfw");
        Assert.Contains(specs, spec => spec.Extension == ".uotannode" && FileAssociationService.LinuxIconName(spec) == "uotannode");
    }

    [Fact]
    public void BuildMimeXml_DeclaresIconForEachMimeType()
    {
        var xml = FileAssociationService.BuildMimeXml(FileAssociationService.BuildSpecs([".apk", ".xyz"]));

        Assert.Contains("<icon name=\"apk\"/>", xml);
        Assert.Contains("<icon name=\"file\"/>", xml);
    }

    [Fact]
    public void BuildMacDocumentTypes_DeclaresBundledIconFile()
    {
        var types = FileAssociationService.BuildMacDocumentTypes(FileAssociationService.BuildSpecs([".apk", ".xyz"]));

        Assert.Contains(types, type => type.Extension == ".apk" && type.IconFile == "apk.icns");
        Assert.Contains(types, type => type.Extension == ".xyz" && type.IconFile == "file.icns");
    }
}