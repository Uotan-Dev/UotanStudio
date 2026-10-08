using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class FsConfigPatchTests
{
    [Theory]
    [InlineData("", "file 1000 2000 0755 0\n")]
    [InlineData("# keep\r\n", "# keep\r\nfile 1000 2000 0755 0\r\n")]
    [InlineData("# keep", "# keep\nfile 1000 2000 0755 0\n")]
    public void Add_MissingPath_AppendsOctalTokenAndRepeats(string text, string expected)
    {
        var token = TestContext.Current.CancellationToken;
        var result = FsConfigPatch.Add(text, "/file", "1000", "2000", "0755", token);
        Assert.Equal(expected, result.Text);
        Assert.Equal(0, result.Matches);
        Assert.False(FsConfigPatch.Add(result.Text, "file", "1000", "2000", "755", token).Changed);
    }

    [Fact]
    public void Add_ExistingOpaqueCapabilityData_PreservesSourceAndRejectsOwnershipConflict()
    {
        const string text = "file 0 0 0755 capabilities=0x1";
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(text, FsConfigPatch.Add(text, "file", "0", "0", "0755", token).Text);
        Assert.Throws<InvalidDataException>(() => FsConfigPatch.Add(text, "file", "1", "0", "0755", token));
        Assert.Throws<InvalidDataException>(() => FsConfigPatch.Add("", "#bad", "0", "0", "0755", token));
    }

    [Fact]
    public void Delete_EntryLine_PreservesAdjacentCommentsAndGuardsMissingRepeat()
    {
        var token = TestContext.Current.CancellationToken;
        const string text = "# before\r\n file 0 0 0755 0 # remove\r\n# after\r\n";
        var result = FsConfigPatch.Delete(text, "/file", true, "0", "0", "0755", token);
        Assert.Equal("# before\r\n# after\r\n", result.Text);
        Assert.False(FsConfigPatch.Delete(result.Text, "file", false, "", "", "", token).Changed);
        Assert.Throws<InvalidDataException>(() => FsConfigPatch.Delete(result.Text, "file", true, "0", "0", "0755", token));
        Assert.Throws<InvalidDataException>(() => FsConfigPatch.Delete(text, "file", true, "1", "0", "0755", token));
    }

    [Theory]
    [InlineData("0755", 493)]
    [InlineData("755", 493)]
    [InlineData("4755", 2541)]
    [InlineData("0000", 0)]
    public void Read_OctalModeToken_ReturnsPermissionBits(string mode, ushort expected)
    {
        var entry = FsConfigPatch.Read("vendor/bin/test 1000 2000 " + mode + " 0\n",
            "/vendor/bin/test", TestContext.Current.CancellationToken);
        Assert.Equal(new FsConfigEntry(1000, 2000, expected), entry);
    }

    [Theory]
    [InlineData("493")]
    [InlineData("0855")]
    [InlineData("65535")]
    [InlineData("0x1ed")]
    public void Read_DecimalOrInvalidModeToken_Rejects(string mode)
        => Assert.Throws<PatchingCorruptedException>(() => FsConfigPatch.Read("vendor/bin/test 1000 2000 " + mode + " 0\n",
            "/vendor/bin/test", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData((ushort)0, "0000")]
    [InlineData((ushort)420, "0644")]
    [InlineData((ushort)416, "0640")]
    [InlineData((ushort)493, "0755")]
    [InlineData((ushort)2541, "4755")]
    [InlineData((ushort)348, "0534")]
    [InlineData((ushort)49151, "7777")]
    public void Format_Mode_ReturnsFourDigitOctalTokenAndRoundTrips(ushort mode, string expected)
    {
        Assert.Equal(expected, FsConfigModes.Format(mode));
        Assert.True(FsConfigModes.TryParse(expected, out var parsed));
        Assert.Equal((ushort)(mode & 0xFFF), parsed);
        Assert.Equal(expected, FsConfigModes.Format(parsed));
    }

    [Theory]
    [InlineData("4755", 2541)]
    [InlineData("755", 493)]
    [InlineData("0644", 420)]
    [InlineData("0000", 0)]
    [InlineData("007777", 4095)]
    public void TryParse_OctalToken_ReturnsPermissionBits(string token, ushort expected)
    {
        Assert.True(FsConfigModes.TryParse(token, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("493")]
    [InlineData("6448")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("0755 ")]
    [InlineData("7.55")]
    public void TryParse_NonOctalToken_ReturnsFalse(string token)
        => Assert.False(FsConfigModes.TryParse(token, out _));

    [Fact]
    public async Task Edit_RealBuilderReader_ObservesNewOwnershipAndPermissions()
    {
        const string text = "# keep\r\nvendor/bin/test  0  0  0755 capabilities=0x1\r\nother 0 0 0644 0\r\n";
        var result = FsConfigPatch.Edit(text, "vendor/bin/test", "1000", "2000", "640", true, "0", "0", "0755",
            TestContext.Current.CancellationToken);
        Assert.Equal(text.Replace("0  0  0755", "1000  2000  0640", StringComparison.Ordinal), result.Text);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "_fs_config");
        try
        {
            await File.WriteAllTextAsync(path, result.Text, TestContext.Current.CancellationToken);
            Assert.Equal(new FsConfigEntry(1000, 2000, 416), FileSystemMetadataOverrides.LoadFsConfig(path)["vendor/bin/test"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Edit_EquivalentBareOctalMode_PreservesOriginalText()
    {
        const string text = "file 0 0 0755 0";
        Assert.False(FsConfigPatch.Edit(text, "file", "0", "0", "755", false, "", "", "",
            TestContext.Current.CancellationToken).Changed);
    }

    [Fact]
    public void Rebase_AppendedDefault_WritesOctalModeToken()
    {
        var result = FsConfigPatch.Rebase("bin/sh 0 0 0755 0\n", "system", "my_product",
            TestContext.Current.CancellationToken);
        Assert.Contains("system/my_product 0 0 0755", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("493", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file 0 0 0755\n/file 0 0 0755")]
    [InlineData("file\t0 0 0644")]
    public void Read_AmbiguousOrInvalidMetadata_Rejects(string text)
        => Assert.Throws<InvalidDataException>(() => FsConfigPatch.Read(text, "file", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("file 0 0 0999")]
    [InlineData("file 0 0 65535")]
    [InlineData("file -1 0 0644")]
    public void Read_CorruptFieldValues_Rejects(string text)
        => Assert.Throws<PatchingCorruptedException>(() => FsConfigPatch.Read(text, "file", TestContext.Current.CancellationToken));
}