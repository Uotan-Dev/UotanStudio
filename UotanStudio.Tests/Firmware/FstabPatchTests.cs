using UotanStudio.Firmware.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class FstabPatchTests
{
    [Theory]
    [InlineData("")]
    [InlineData("# header")]
    [InlineData("# header\r\n")]
    public void Add_MissingEntry_PreservesPrefixAndIsIdempotent(string prefix)
    {
        var token = TestContext.Current.CancellationToken;
        var first = FstabPatch.Add(prefix, "system", "/system", "ext4", "ro", "wait,logical", token);
        var second = FstabPatch.Add(first.Text, "system", "/system", "ext4", "ro", "wait,logical", token);
        var newline = prefix.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        Assert.Equal(prefix + (prefix.Length > 0 && !prefix.EndsWith('\n') ? newline : "")
            + "system /system ext4 ro wait,logical" + newline, first.Text);
        Assert.True(first.Changed);
        Assert.Equal(0, first.Matches);
        Assert.Equal(first.Text, second.Text);
        Assert.False(second.Changed);
        Assert.Equal(1, second.Matches);
    }

    [Fact]
    public void Add_ConflictingPair_RejectsButAllowsAnotherFilesystem()
    {
        const string source = "system\t/system erofs ro wait # keep\n";
        var token = TestContext.Current.CancellationToken;
        Assert.Throws<InvalidDataException>(() => FstabPatch.Add(source, "other", "/system", "erofs", "ro", "wait", token));
        Assert.Throws<InvalidDataException>(() => FstabPatch.Add(source + source, "system", "/system", "erofs", "ro", "wait", token));
        Assert.Throws<InvalidDataException>(() => FstabPatch.Add(source, "system", "/system", "ext4", "ro\nother", "wait", token));
        Assert.StartsWith(source, FstabPatch.Add(source, "system", "/system", "ext4", "ro", "wait", token).Text);
        Assert.Equal(source, FstabPatch.Add(source, "system", "/system", "erofs", "ro", "wait", token).Text);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Apply_DeleteEntry_PreservesAdjacentCommentsAndFallback(string newline)
    {
        var text = "# header" + newline + "  system /system erofs ro wait # inline" + newline
            + "# fallback" + newline + "system /system ext4 ro wait";
        var result = FstabPatch.Apply(text, "/system", "erofs", FstabField.FileSystem, FstabOperation.DeleteEntry,
            "", "", true, "erofs", 1, TestContext.Current.CancellationToken);
        Assert.Equal("# header" + newline + "# fallback" + newline + "system /system ext4 ro wait", result.Text);
        Assert.Equal(1, result.Matches);
        Assert.True(result.Changed);
    }

    [Fact]
    public void Apply_DeleteFinalEntryWithoutNewline_RemovesWholeLine()
    {
        var result = FstabPatch.Apply("  system /system ext4 ro wait", "/system", "", FstabField.FsMgrFlags,
            FstabOperation.DeleteEntry, "", "", true, "wait", 1, TestContext.Current.CancellationToken);
        Assert.Empty(result.Text);
        Assert.Throws<InvalidDataException>(() => FstabPatch.Apply("system /system ext4 ro wait", "/system", "",
            FstabField.FsMgrFlags, FstabOperation.DeleteEntry, "", "", true, "different", 1, TestContext.Current.CancellationToken));
    }

    private const string Sample = "# vendor fstab\r\n system\t/system  erofs ro wait,logical,first_stage_mount # keep\r\nsystem /system ext4 ro wait,logical\r\n";

    [Fact]
    public void Apply_SelectedFilesystemFlag_PreservesOtherColumnsAndFallbackEntry()
    {
        var result = FstabPatch.Apply(Sample, "/system", "erofs", FstabField.FsMgrFlags, FstabOperation.SetFlag,
            "vbmeta_system", "avb", true, "wait,logical,first_stage_mount", 1, TestContext.Current.CancellationToken);
        Assert.Equal(Sample.Replace("first_stage_mount", "first_stage_mount,avb=vbmeta_system", StringComparison.Ordinal), result.Text);
        Assert.Equal(1, result.Matches);
        Assert.True(result.Changed);
        Assert.Equal(new[] { "ro", "ro" }, FstabPatch.Read(result.Text, "/system", "", FstabField.MountOptions, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_DeleteLastFlag_UsesDefaultsAndIsRepeatable()
    {
        const string text = "system /system ext4 ro wait\n";
        var first = FstabPatch.Apply(text, "/system", "", FstabField.FsMgrFlags, FstabOperation.DeleteFlag,
            "", "wait", false, "", 1, TestContext.Current.CancellationToken);
        var second = FstabPatch.Apply(first.Text, "/system", "", FstabField.FsMgrFlags, FstabOperation.DeleteFlag,
            "", "wait", false, "", 1, TestContext.Current.CancellationToken);
        Assert.Equal("system /system ext4 ro defaults\n", first.Text);
        Assert.False(second.Changed);
    }

    [Theory]
    [InlineData("system /system ext4 ro wait\nsystem /system erofs ro wait", 1)]
    [InlineData("system /system ext4 ro wait", 0)]
    public void Read_MalformedOrAmbiguousSelection_Rejects(string text, int expected)
    {
        Assert.Throws<InvalidDataException>(() => FstabPatch.Read(text, "/system", "", FstabField.BlockDevice,
            expected, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_MalformedFieldCount_Rejects()
        => Assert.Throws<PatchingCorruptedException>(() => FstabPatch.Read("system /system ext4 ro", "/system", "",
            FstabField.BlockDevice, 1, TestContext.Current.CancellationToken));

    [Fact]
    public void Apply_OldValueMismatchOrDuplicateFlag_Rejects()
    {
        Assert.Throws<InvalidDataException>(() => FstabPatch.Apply(Sample, "/system", "erofs", FstabField.BlockDevice,
            FstabOperation.SetValue, "vendor", "", true, "wrong", 1, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => FstabPatch.Apply("x /system ext4 ro wait,wait", "/system", "", FstabField.FsMgrFlags,
            FstabOperation.DeleteFlag, "", "wait", false, "", 1, TestContext.Current.CancellationToken));
    }
}