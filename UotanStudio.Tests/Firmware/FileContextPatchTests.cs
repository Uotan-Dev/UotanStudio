using UotanStudio.Firmware.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class FileContextPatchTests
{
    [Theory]
    [InlineData("", "/path -- u:object_r:file:s0\n")]
    [InlineData("# keep", "# keep\n/path -- u:object_r:file:s0\n")]
    [InlineData("# keep\r\n", "# keep\r\n/path -- u:object_r:file:s0\r\n")]
    public void Add_MissingRule_AppendsOnce(string text, string expected)
    {
        var token = TestContext.Current.CancellationToken;
        var result = FileContextPatch.Add(text, "/path", "--", "u:object_r:file:s0", token);
        Assert.Equal(expected, result.Text);
        Assert.Equal(0, result.Matches);
        Assert.False(FileContextPatch.Add(result.Text, "/path", "--", "u:object_r:file:s0", token).Changed);
    }

    [Fact]
    public void Add_DifferentQualifier_CoexistsButSameIdentityConflicts()
    {
        const string text = "/path u:object_r:file:s0\n";
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(text + "/path -d u:object_r:directory:s0\n",
            FileContextPatch.Add(text, "/path", "-d", "u:object_r:directory:s0", token).Text);
        Assert.Throws<InvalidDataException>(() => FileContextPatch.Add(text, "/path", "", "u:object_r:other:s0", token));
    }

    [Fact]
    public void Delete_QualifiedRule_PreservesCommentsAndOtherKinds()
    {
        const string text = "# keep\r\n/path -- u:object_r:file:s0\r\n/path -d u:object_r:directory:s0\r\n";
        var token = TestContext.Current.CancellationToken;
        var result = FileContextPatch.Delete(text, "/path", "--", true, "u:object_r:file:s0", token);
        Assert.Equal("# keep\r\n/path -d u:object_r:directory:s0\r\n", result.Text);
        Assert.False(FileContextPatch.Delete(result.Text, "/path", "--", false, "", token).Changed);
        Assert.Throws<InvalidDataException>(() => FileContextPatch.Delete(result.Text, "/path", "--", true, "u:object_r:file:s0", token));
        Assert.Throws<InvalidDataException>(() => FileContextPatch.Delete(text, "/path", "--", true, "u:object_r:other:s0", token));
    }
    [Fact]
    public void Edit_QualifiedRegexRule_PreservesOtherKindsOrderAndFormatting()
    {
        const string text = "# keep\r\n/vendor/bin/.*  u:object_r:vendor_file:s0\r\n/vendor/bin/.*\t--\tu:object_r:old_exec:s0\r\n";
        var result = FileContextPatch.Edit(text, "/vendor/bin/.*", "--", "u:object_r:new_exec:s0", true,
            "u:object_r:old_exec:s0", TestContext.Current.CancellationToken);
        Assert.Equal(text.Replace("old_exec", "new_exec", StringComparison.Ordinal), result.Text);
        Assert.Equal("u:object_r:vendor_file:s0", FileContextPatch.Read(result.Text, "/vendor/bin/.*", "", TestContext.Current.CancellationToken));
        Assert.False(FileContextPatch.Edit(result.Text, "/vendor/bin/.*", "--", "u:object_r:new_exec:s0", false, "",
            TestContext.Current.CancellationToken).Changed);
    }

    [Theory]
    [InlineData("<<none>>")]
    [InlineData("u:object_r:vendor_file:s0:c1,c2")]
    [InlineData("user_u:object_r:file_t")]
    public void Read_ContextForms_ReturnsUnchangedToken(string context)
        => Assert.Equal(context, FileContextPatch.Read("/path " + context, "/path", "", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("/path u:object_r:a:s0\n/path u:object_r:b:s0")]
    public void Read_DuplicateRules_Rejects(string text)
        => Assert.Throws<InvalidDataException>(() => FileContextPatch.Read(text, "/path", "", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("/path -x u:object_r:a:s0")]
    public void Read_UnsupportedFileKindQualifier_Rejects(string text)
        => Assert.Throws<PatchingUnsupportedFeatureException>(() => FileContextPatch.Read(text, "/path", "", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("/path malformed")]
    [InlineData("/path u:object_r:a:s0 extra extra")]
    public void Read_MalformedRules_Rejects(string text)
        => Assert.Throws<PatchingCorruptedException>(() => FileContextPatch.Read(text, "/path", "", TestContext.Current.CancellationToken));

    [Fact]
    public void Edit_OldValueMismatchOrInjectedLine_Rejects()
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Throws<InvalidDataException>(() => FileContextPatch.Edit("/path u:object_r:a:s0", "/path", "",
            "u:object_r:b:s0", true, "u:object_r:c:s0", token));
        Assert.Throws<PatchingCorruptedException>(() => FileContextPatch.Edit("/path u:object_r:a:s0", "/path", "",
            "u:object_r:b:s0\n/injected u:object_r:b:s0", false, "", token));
    }
}