using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class FsConfigPatchTests
{
    [Theory]
    [InlineData("", "file 1000 2000 493 0\n")]
    [InlineData("# keep\r\n", "# keep\r\nfile 1000 2000 493 0\r\n")]
    [InlineData("# keep", "# keep\nfile 1000 2000 493 0\n")]
    public void Add_MissingPath_AppendsAndRepeats(string text, string expected)
    {
        var token = TestContext.Current.CancellationToken;
        var result = FsConfigPatch.Add(text, "/file", "1000", "2000", "0755", token);
        Assert.Equal(expected, result.Text);
        Assert.Equal(0, result.Matches);
        Assert.False(FsConfigPatch.Add(result.Text, "file", "1000", "2000", "493", token).Changed);
    }

    [Fact]
    public void Add_ExistingOpaqueCapabilityData_PreservesSourceAndRejectsOwnershipConflict()
    {
        const string text = "file 0 0 0755 capabilities=0x1";
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(text, FsConfigPatch.Add(text, "file", "0", "0", "493", token).Text);
        Assert.Throws<InvalidDataException>(() => FsConfigPatch.Add(text, "file", "1", "0", "493", token));
        Assert.Throws<InvalidDataException>(() => FsConfigPatch.Add("", "#bad", "0", "0", "493", token));
    }

    [Fact]
    public void Delete_EntryLine_PreservesAdjacentCommentsAndGuardsMissingRepeat()
    {
        var token = TestContext.Current.CancellationToken;
        const string text = "# before\r\n file 0 0 493 0 # remove\r\n# after\r\n";
        var result = FsConfigPatch.Delete(text, "/file", true, "0", "0", "0755", token);
        Assert.Equal("# before\r\n# after\r\n", result.Text);
        Assert.False(FsConfigPatch.Delete(result.Text, "file", false, "", "", "", token).Changed);
        Assert.Throws<InvalidDataException>(() => FsConfigPatch.Delete(result.Text, "file", true, "0", "0", "0755", token));
        Assert.Throws<InvalidDataException>(() => FsConfigPatch.Delete(text, "file", true, "1", "0", "0755", token));
    }
    [Theory]
    [InlineData("493")]
    [InlineData("0755")]
    public void Read_ModeDialect_ReturnsBuilderPermissions(string mode)
        => Assert.Equal(new FsConfigEntry(1000, 2000, 493), FsConfigPatch.Read("vendor/bin/test 1000 2000 " + mode + " 0\n",
            "/vendor/bin/test", TestContext.Current.CancellationToken));

    [Fact]
    public async Task Edit_RealBuilderReader_ObservesNewOwnershipAndPermissions()
    {
        const string text = "# keep\r\nvendor/bin/test  0  0  493 capabilities=0x1\r\nother 0 0 420 0\r\n";
        var result = FsConfigPatch.Edit(text, "vendor/bin/test", "1000", "2000", "0640", true, "0", "0", "0755",
            TestContext.Current.CancellationToken);
        Assert.Equal(text.Replace("0  0  493", "1000  2000  416", StringComparison.Ordinal), result.Text);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "_fs_config");
        try
        {
            await File.WriteAllTextAsync(path, result.Text, TestContext.Current.CancellationToken);
            Assert.Equal(new FsConfigEntry(1000, 2000, 416), FileSystemMetadataOverrides.LoadFsConfig(path)["vendor/bin/test"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Edit_EquivalentMode_PreservesOriginalText()
    {
        const string text = "file 0 0 0755 0";
        Assert.False(FsConfigPatch.Edit(text, "file", "0", "0", "493", false, "", "", "",
            TestContext.Current.CancellationToken).Changed);
    }

    [Theory]
    [InlineData("file 0 0 493\n/file 0 0 493")]
    [InlineData("file 0 0 0999")]
    [InlineData("file 0 0 65535")]
    [InlineData("file -1 0 420")]
    [InlineData("file\t0 0 420")]
    public void Read_AmbiguousOrInvalidMetadata_Rejects(string text)
        => Assert.Throws<InvalidDataException>(() => FsConfigPatch.Read(text, "file", TestContext.Current.CancellationToken));
}