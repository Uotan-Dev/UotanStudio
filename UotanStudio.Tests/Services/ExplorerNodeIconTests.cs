using Material.Icons;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Targeted tests for explorer node icons: the outline-first mapping rule, the family colours, and the
/// directory/project-folder split.
/// <para>资源管理器节点图标的定向测试：空心图标优先的映射规则、家族配色，以及目录/项目文件夹的区分。</para>
/// </summary>
public sealed class ExplorerNodeIconTests
{
    [Fact]
    public void IconKind_ForDirectoriesAndProjectFolders_UsesOutlineFolderGlyphs()
    {
        var directory = new ExplorerNode("images", isDirectory: true);
        var project = new ExplorerNode("images", isDirectory: true, isProjectFolder: true);

        Assert.Equal(MaterialIconKind.FolderOutline, directory.IconKind);
        Assert.Equal(MaterialIconKind.FolderStarOutline, project.IconKind);
        Assert.Same(ExplorerIconPalette.Folder, directory.IconBrush);
        Assert.Same(ExplorerIconPalette.Folder, project.IconBrush);
    }

    [Theory]
    [InlineData("dtbo.dtbo", MaterialIconKind.FileTreeOutline)]
    [InlineData("classes.dex", MaterialIconKind.FileCodeOutline)]
    [InlineData("framework.jar", MaterialIconKind.FileCodeOutline)]
    [InlineData("resources.arsc", MaterialIconKind.DatabaseOutline)]
    [InlineData("system.new.dat.br", MaterialIconKind.ArchiveOutline)]
    [InlineData("vendor.zip", MaterialIconKind.ZipBoxOutline)]
    [InlineData("firmware.pac", MaterialIconKind.PackageOutline)]
    [InlineData("release.jks", MaterialIconKind.FileKeyOutline)]
    [InlineData("platform.pem", MaterialIconKind.FileCertificateOutline)]
    [InlineData("logo.png", MaterialIconKind.FileImageOutline)]
    [InlineData("boot_logo.mp4", MaterialIconKind.MovieOutline)]
    [InlineData("notification.ogg", MaterialIconKind.FileMusicOutline)]
    [InlineData("updater-script.sh", MaterialIconKind.ScriptOutline)]
    [InlineData("build.prop", MaterialIconKind.FileCogOutline)]
    [InlineData("README.md", MaterialIconKind.LanguageMarkdownOutline)]
    [InlineData("partitions.csv", MaterialIconKind.FileDelimitedOutline)]
    [InlineData("manual.docx", MaterialIconKind.FileWordOutline)]
    [InlineData("inventory.xlsx", MaterialIconKind.FileExcelOutline)]
    [InlineData("slides.pptx", MaterialIconKind.FilePowerpointOutline)]
    [InlineData("device.log", MaterialIconKind.TextBoxMultipleOutline)]
    [InlineData("notes.txt", MaterialIconKind.FileDocumentOutline)]
    [InlineData("super.img", MaterialIconKind.Harddisk)]
    [InlineData("untrusted.ko", MaterialIconKind.Chip)]
    [InlineData("app-debug.apk", MaterialIconKind.Android)]
    [InlineData("AndroidManifest.axml", MaterialIconKind.FileXmlBox)]
    [InlineData("device_config.xml", MaterialIconKind.Xml)]
    [InlineData("config.json", MaterialIconKind.CodeJson)]
    [InlineData("manual.pdf", MaterialIconKind.FilePdfBox)]
    public void IconKind_ForKnownFileTypes_UsesOutlineGlyphWhenAvailable(string fileName, MaterialIconKind expected)
    {
        Assert.Equal(expected, new ExplorerNode(fileName, isDirectory: false).IconKind);
    }

    [Fact]
    public void IconKind_ForKnownFileTypes_MatchesExtensionCaseInsensitively()
    {
        Assert.Equal(MaterialIconKind.Harddisk, new ExplorerNode("SUPER.IMG", isDirectory: false).IconKind);
        Assert.Equal(MaterialIconKind.FileTreeOutline, new ExplorerNode("DTBO.DTBO", isDirectory: false).IconKind);
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
    public void IconKind_ForTransferList_UsesOutlineBlockTransferGlyph()
    {
        var transferList = Node("system.transfer.list");

        Assert.Equal(MaterialIconKind.FileArrowUpDownOutline, transferList.IconKind);
        Assert.Same(ExplorerIconPalette.Image, transferList.IconBrush);
        Assert.Equal(MaterialIconKind.FileOutline, Node("playlist.list").IconKind);
    }

    [Fact]
    public void IconKind_ForUnknownOrPlaceholderNodes_UsesGenericFileGlyph()
    {
        var unknown = Node("noextension");
        var placeholder = new ExplorerNode(string.Empty, isDirectory: false, isPlaceholder: true);

        Assert.Equal(MaterialIconKind.FileOutline, unknown.IconKind);
        Assert.Equal(MaterialIconKind.FileOutline, placeholder.IconKind);
        Assert.Same(ExplorerIconPalette.Document, unknown.IconBrush);
        Assert.Same(ExplorerIconPalette.Document, placeholder.IconBrush);
    }

    private static ExplorerNode Node(string fileName) => new(fileName, isDirectory: false);
}