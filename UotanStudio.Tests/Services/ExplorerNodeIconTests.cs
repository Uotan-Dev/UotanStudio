using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Targeted tests for explorer node icons: the extension mapping, the family colours, and the directory/project-folder split.
/// <para>资源管理器节点图标的定向测试：扩展名映射、家族配色，以及目录/项目文件夹的区分。</para>
/// </summary>
public sealed class ExplorerNodeIconTests
{
    [Fact]
    public void IconKind_ForDirectoriesAndProjectFolders_UsesFolderGlyphs()
    {
        var directory = new ExplorerNode("images", isDirectory: true);
        var project = new ExplorerNode("images", isDirectory: true, isProjectFolder: true);

        Assert.Equal(AppIconKind.Folder, directory.IconKind);
        Assert.Equal(AppIconKind.ProjectFolder, project.IconKind);
        Assert.Same(ExplorerIconPalette.Folder, directory.IconBrush);
        Assert.Same(ExplorerIconPalette.Folder, project.IconBrush);
    }

    [Theory]
    [InlineData("dtbo.dtbo", AppIconKind.DeviceTree)]
    [InlineData("classes.dex", AppIconKind.FileCode)]
    [InlineData("framework.jar", AppIconKind.FileCode)]
    [InlineData("resources.arsc", AppIconKind.Database)]
    [InlineData("system.new.dat.br", AppIconKind.Archive)]
    [InlineData("vendor.zip", AppIconKind.Zip)]
    [InlineData("firmware.pac", AppIconKind.Package)]
    [InlineData("release.jks", AppIconKind.Key)]
    [InlineData("platform.pem", AppIconKind.Certificate)]
    [InlineData("logo.png", AppIconKind.Image)]
    [InlineData("boot_logo.mp4", AppIconKind.Movie)]
    [InlineData("notification.ogg", AppIconKind.Music)]
    [InlineData("updater-script.sh", AppIconKind.Script)]
    [InlineData("build.prop", AppIconKind.FileCog)]
    [InlineData("README.md", AppIconKind.Markdown)]
    [InlineData("partitions.csv", AppIconKind.Csv)]
    [InlineData("manual.docx", AppIconKind.Word)]
    [InlineData("inventory.xlsx", AppIconKind.Excel)]
    [InlineData("slides.pptx", AppIconKind.PowerPoint)]
    [InlineData("device.log", AppIconKind.TextLines)]
    [InlineData("notes.txt", AppIconKind.File)]
    [InlineData("super.img", AppIconKind.HardDisk)]
    [InlineData("untrusted.ko", AppIconKind.Chip)]
    [InlineData("app-debug.apk", AppIconKind.Android)]
    [InlineData("AndroidManifest.axml", AppIconKind.FileXml)]
    [InlineData("device_config.xml", AppIconKind.Xml)]
    [InlineData("config.json", AppIconKind.Json)]
    [InlineData("manual.pdf", AppIconKind.Pdf)]
    public void IconKind_ForKnownFileTypes_UsesCatalogGlyph(string fileName, AppIconKind expected)
    {
        Assert.Equal(expected, new ExplorerNode(fileName, isDirectory: false).IconKind);
    }

    [Fact]
    public void IconKind_ForKnownFileTypes_MatchesExtensionCaseInsensitively()
    {
        Assert.Equal(AppIconKind.HardDisk, new ExplorerNode("SUPER.IMG", isDirectory: false).IconKind);
        Assert.Equal(AppIconKind.DeviceTree, new ExplorerNode("DTBO.DTBO", isDirectory: false).IconKind);
    }

    [Fact]
    public void IconBrush_ForEachFamily_UsesItsPaletteColour()
    {
        Assert.Same(ExplorerIconPalette.Image, Node("super.img").IconBrush);
        Assert.Same(ExplorerIconPalette.Firmware, Node("untrusted.ko").IconBrush);
        Assert.Same(ExplorerIconPalette.Firmware, Node("firmware.pac").IconBrush);
        Assert.Same(ExplorerIconPalette.DeviceTree, Node("dtbo.dtbo").IconBrush);
        Assert.Same(ExplorerIconPalette.Android, Node("app-debug.apk").IconBrush);
        Assert.Same(ExplorerIconPalette.Code, Node("classes.dex").IconBrush);
        Assert.Same(ExplorerIconPalette.Resource, Node("resources.arsc").IconBrush);
        Assert.Same(ExplorerIconPalette.Markup, Node("AndroidManifest.axml").IconBrush);
        Assert.Same(ExplorerIconPalette.Markup, Node("device_config.xml").IconBrush);
        Assert.Same(ExplorerIconPalette.Json, Node("config.json").IconBrush);
        Assert.Same(ExplorerIconPalette.Script, Node("updater-script.sh").IconBrush);
        Assert.Same(ExplorerIconPalette.Archive, Node("system.new.dat.br").IconBrush);
        Assert.Same(ExplorerIconPalette.Security, Node("release.jks").IconBrush);
        Assert.Same(ExplorerIconPalette.Media, Node("logo.png").IconBrush);
        Assert.Same(ExplorerIconPalette.Document, Node("build.prop").IconBrush);
        Assert.Same(ExplorerIconPalette.Document, Node("notes.txt").IconBrush);
        Assert.Same(ExplorerIconPalette.Pdf, Node("manual.pdf").IconBrush);
    }

    [Fact]
    public void IconKind_ForTransferList_UsesTransferGlyph()
    {
        var transferList = Node("system.transfer.list");

        Assert.Equal(AppIconKind.FileTransfer, transferList.IconKind);
        Assert.Same(ExplorerIconPalette.Image, transferList.IconBrush);
        Assert.Equal(AppIconKind.File, Node("playlist.list").IconKind);
    }

    [Fact]
    public void IconKind_ForUnknownOrPlaceholderNodes_UsesGenericFileGlyph()
    {
        var unknown = Node("noextension");
        var placeholder = new ExplorerNode(string.Empty, isDirectory: false, isPlaceholder: true);

        Assert.Equal(AppIconKind.File, unknown.IconKind);
        Assert.Equal(AppIconKind.File, placeholder.IconKind);
        Assert.Same(ExplorerIconPalette.Document, unknown.IconBrush);
        Assert.Same(ExplorerIconPalette.Document, placeholder.IconBrush);
    }

    private static ExplorerNode Node(string fileName) => new(fileName, isDirectory: false);
}