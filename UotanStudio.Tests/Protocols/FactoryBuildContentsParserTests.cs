using UotanStudio.Comm.Protocols.Edl.FlashBundle.FactoryBuild;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public class FactoryBuildContentsParserTests
{
    private static string TempXml(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-factory-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }

    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <contents>
          <header>
            <chip_id>kalama</chip_id>
            <build_id>BLDR-123</build_id>
            <flavor>asic</flavor>
            <flavor>core_asic</flavor>
          </header>
          <image name="xbl" filename="xbl.elf"/>
          <image name="tz" filename="tz.mbn" flavor="asic"/>
          <image name="fsc" filename=""/>
        </contents>
        """;

    [Fact]
    public async Task ParseAsync_Sample_ParsesHeaderAndImages()
    {
        var path = TempXml(Sample);
        try
        {
            var contents = await FactoryBuildContentsParser.ParseAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("kalama", contents.ChipId);
            Assert.Equal("BLDR-123", contents.BuildId);
            Assert.Equal(2, contents.Flavors.Count);
            Assert.Equal(3, contents.Images.Count);
            Assert.Equal("xbl", contents.Images[0].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FlattenAsync_FlavorFiltersImages()
    {
        var path = TempXml(Sample);
        try
        {
            var contents = await FactoryBuildContentsParser.ParseAsync(path, TestContext.Current.CancellationToken);
            var buildRoot = Path.GetDirectoryName(path)!;
            await File.WriteAllBytesAsync(Path.Combine(buildRoot, "xbl.elf"), [1, 2, 3], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(buildRoot, "tz.mbn"), [4, 5], TestContext.Current.CancellationToken);
            var plan = await contents.FlattenAsync("core_asic", buildRoot, TestContext.Current.CancellationToken);
            Assert.Equal(2, plan.Operations.Count);
            Assert.Equal("xbl", plan.Operations[0].Partition);
            Assert.Equal(3, plan.Operations[0].ImageBytes);
            Assert.Equal("fsc", plan.Operations[1].Partition);
            Assert.Null(plan.Operations[1].ImagePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FlattenAsync_UnknownFlavor_Throws()
    {
        var path = TempXml(Sample);
        try
        {
            var contents = await FactoryBuildContentsParser.ParseAsync(path, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                contents.FlattenAsync("does-not-exist", Path.GetDirectoryName(path)!, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_MissingHeader_Throws()
    {
        var path = TempXml("<contents><image name='a' filename='a.bin'/></contents>");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FactoryBuildContentsParser.ParseAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_ParentTraversalFileName_Throws()
    {
        var path = TempXml("<contents><header><chip_id>c</chip_id><build_id>b</build_id></header><image name='a' filename='../a.bin'/></contents>");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FactoryBuildContentsParser.ParseAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}