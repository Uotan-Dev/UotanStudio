using UotanStudio.Services.Firmware;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Targeted tests for the format-conversion scope: which files show the explorer's "convert format" entry
/// and where that decision lives.
/// <para>格式转换范围的定向测试：哪些文件显示资源管理器「格式转换」入口，以及该判定归属何处。</para>
/// </summary>
public sealed class ExplorerConversionScopeTests
{
    [Theory]
    [InlineData("system.img")]
    [InlineData("boot.raw")]
    [InlineData("payload.bin")]
    [InlineData("super.simg")]
    [InlineData("userdata.sparse")]
    [InlineData("super.img.super")]
    [InlineData("partition.image")]
    [InlineData("SYSTEM.IMG")]
    public void SupportsFormatConversion_PartitionAndRawImages_ReturnsTrue(string fileName)
    {
        Assert.True(ImageInputService.SupportsFormatConversion(fileName));
    }

    [Theory]
    [InlineData("installer.iso")]
    [InlineData("system.new.dat")]
    [InlineData("system.new.dat.br")]
    [InlineData("system.patch.dat")]
    [InlineData("system.patch.dat.br")]
    [InlineData("system.transfer.list")]
    [InlineData("notes.txt")]
    [InlineData("noextension")]
    public void SupportsFormatConversion_NonConversionInputs_ReturnsFalse(string fileName)
    {
        Assert.False(ImageInputService.SupportsFormatConversion(fileName));
    }

    [Fact]
    public void SupportsFormatConversion_EmptyPath_ReturnsFalse()
    {
        Assert.False(ImageInputService.SupportsFormatConversion(string.Empty));
    }

    [Fact]
    public void ExplorerNode_SupportsFormatConversion_DirectoryAndPlaceholder_ReturnFalse()
    {
        var directory = new ExplorerNode("system.img", isDirectory: true);
        var placeholder = new ExplorerNode(string.Empty, isDirectory: false, isPlaceholder: true);
        var file = new ExplorerNode("system.img", isDirectory: false);

        Assert.False(directory.SupportsFormatConversion);
        Assert.False(placeholder.SupportsFormatConversion);
        Assert.True(file.SupportsFormatConversion);
    }

    [Fact]
    public void ExplorerIconCatalog_OpticalDiscImage_KeepsDiskGlyphWhileConversionStaysClosed()
    {
        Assert.Equal(AppIconKind.HardDisk, new ExplorerNode("installer.iso", isDirectory: false).IconKind);
        Assert.False(new ExplorerNode("installer.iso", isDirectory: false).SupportsFormatConversion);
    }
}