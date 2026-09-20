using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Files;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Canonical DAT/BR file-set naming tests: suffix recognition, longest-first stem stripping, case
/// insensitivity and the classification split used by the firmware handler and the delivery package
/// builder alike.
/// <para>DAT/BR 文件集规范命名测试：后缀识别、长后缀优先的词干剥离、大小写不敏感，以及固件处理器与交付打包
/// 构建器共用的分类判定。</para>
/// </summary>
public class DatFileSetTests
{
    [Theory]
    [InlineData("system.transfer.list", true, false, false)]
    [InlineData("system.new.dat", true, false, true)]
    [InlineData("system.new.dat.br", true, false, true)]
    [InlineData("system.patch.dat", true, true, false)]
    [InlineData("system.patch.dat.br", true, true, false)]
    [InlineData("random.dat", false, false, false)]
    [InlineData("archive.br", false, false, false)]
    [InlineData("boot.img", false, false, false)]
    public void Classification_MatchesArtifactRole(string fileName, bool artifact, bool patch, bool newData)
    {
        Assert.Equal(artifact, DatFileSet.IsArtifact(fileName));
        Assert.Equal(DatFileSet.IsTransferList(fileName), fileName.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(patch, DatFileSet.IsPatchData(fileName));
        Assert.Equal(newData, DatFileSet.IsNewData(fileName));
    }

    [Theory]
    [InlineData("system.transfer.list", "system")]
    [InlineData("system.new.dat", "system")]
    [InlineData("system.new.dat.br", "system")]
    [InlineData("system.patch.dat", "system")]
    [InlineData("system.patch.dat.br", "system")]
    [InlineData("vendor_a.new.dat.br", "vendor_a")]
    [InlineData("boot.img", "boot.img")]
    [InlineData("random.dat", "random.dat")]
    public void Stem_StripsLongestSuffixFirst(string fileName, string expected)
    {
        Assert.Equal(expected, DatFileSet.Stem(fileName));
    }

    [Theory]
    [InlineData("SYSTEM.NEW.DAT.BR", "SYSTEM")]
    [InlineData("System.Transfer.LIST", "System")]
    public void Stem_IsCaseInsensitive(string fileName, string expected)
    {
        Assert.Equal(expected, DatFileSet.Stem(fileName));
    }

    [Fact]
    public void IsBrotli_DistinguishesCompressedDataFromPlain()
    {
        Assert.True(DatFileSet.IsBrotli("system.new.dat.br"));
        Assert.True(DatFileSet.IsBrotli("system.patch.dat.br"));
        Assert.False(DatFileSet.IsBrotli("system.new.dat"));
    }

    [Fact]
    public void OrderedSuffixes_NewBeforeNewBr_PreservesStemForCompressedFiles()
    {
        // ".new.dat" 不是 ".new.dat.br" 的后缀，但长后缀优先仍确保词干剥离稳定。
        // ".new.dat" is not a suffix of ".new.dat.br", but longest-first keeps stem stripping stable.
        Assert.Equal("system", DatFileSet.Stem("system.new.dat.br"));
        Assert.Equal("system", DatFileSet.Stem("system.new.dat"));
    }
}