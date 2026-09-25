using System.Text.Json;
using UotanStudio.Firmware.BoundedContexts.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class StructuredConfigurationPatchTests
{
    [Theory]
    [InlineData("", "{\"a/b\":{\"~key\":[null,true,42]}}")]
    [InlineData("/a~1b/~0key/0", "null")]
    [InlineData("/a~1b/~0key/1", "true")]
    [InlineData("/a~1b/~0key/2", "42")]
    [InlineData("/absent", null)]
    public void Read_JsonPointer_DistinguishesTypedValuesAndMissing(string pointer, string? expected)
    {
        Assert.Equal(expected, JsonPatch.Read("{\"a/b\":{\"~key\":[null,true,42]}}", pointer, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/a/~2")]
    [InlineData("/a/~")]
    [InlineData("a")]
    [InlineData("/a/01")]
    [InlineData("/a/-1")]
    [InlineData("/a/-")]
    [InlineData("/a/2147483648")]
    public void Read_InvalidPointer_RejectsAmbiguousSelection(string pointer)
    {
        Assert.Throws<InvalidDataException>(() => JsonPatch.Read("{\"a\":[1,2]}", pointer, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":{\"x\":1,\"\\u0078\":2}}")]
    public void Read_DuplicateJsonKeys_RejectsDocument(string text)
    {
        Assert.Throws<InvalidDataException>(() => JsonPatch.Read(text, "", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_JsonOldValueAndRepeat_PreservesTypedValuesAndOriginalOnNoChange()
    {
        const string original = "{\n  \"enabled\": false, \"keep\": [1, null, \"x\"]\n}";
        var changed = JsonPatch.Apply(original, "/enabled", "true", false, true, "false", false, TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(changed.Text);
        Assert.True(parsed.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("keep")[1].ValueKind);
        Assert.False(JsonPatch.Apply(changed.Text, "/enabled", "true", false, true, "false", false, TestContext.Current.CancellationToken).Changed);
        Assert.Equal(original, JsonPatch.Apply(original, "/enabled", "false", false, false, "", false, TestContext.Current.CancellationToken).Text);
        Assert.Throws<InvalidDataException>(() => JsonPatch.Apply(original, "/enabled", "true", false, true, "null", false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_JsonCreateDeleteAndNull_RequiresExplicitCreationAndExistingParents()
    {
        Assert.Throws<InvalidDataException>(() => JsonPatch.Apply("{}", "/a", "null", false, false, "", false, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => JsonPatch.Apply("{}", "/a/b", "1", false, false, "", true, TestContext.Current.CancellationToken));
        var added = JsonPatch.Apply("{}", "/a", "null", false, false, "", true, TestContext.Current.CancellationToken);
        Assert.Equal("null", JsonPatch.Read(added.Text, "/a", TestContext.Current.CancellationToken));
        var removed = JsonPatch.Apply(added.Text, "/a", "", true, true, "null", false, TestContext.Current.CancellationToken);
        Assert.Null(JsonPatch.Read(removed.Text, "/a", TestContext.Current.CancellationToken));
        Assert.False(JsonPatch.Apply(removed.Text, "/a", "", true, false, "", false, TestContext.Current.CancellationToken).Changed);
        Assert.Throws<InvalidDataException>(() => JsonPatch.Apply("{}", "", "", true, false, "", false, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => JsonPatch.Apply("[]", "/0", "1", false, false, "", true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_JsonArrayAndRootReplacement_PreservesValidDocument()
    {
        Assert.Throws<InvalidDataException>(() => JsonPatch.Apply("[1,2]", "/0", "", true, false, "", false, TestContext.Current.CancellationToken));
        var changed = JsonPatch.Apply("[1,2,3]", "/1", "{\"x\":true}", false, true, "2", false, TestContext.Current.CancellationToken);
        Assert.Equal("true", JsonPatch.Read(changed.Text, "/1/x", TestContext.Current.CancellationToken));
        var removed = JsonPatch.Apply(changed.Text, "/0", "", true, true, "1", false, TestContext.Current.CancellationToken);
        Assert.Equal("3", JsonPatch.Read(removed.Text, "/1", TestContext.Current.CancellationToken));
        Assert.Equal("null", JsonPatch.Apply(removed.Text, "", "null", false, false, "", false, TestContext.Current.CancellationToken).Text);
    }

    [Fact]
    public void Read_JsonResourceLimits_RejectsExcessiveDepthAndNodes()
    {
        Assert.ThrowsAny<JsonException>(() => JsonPatch.Read(new string('[', 65) + "0" + new string(']', 65), "", TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => JsonPatch.Read("[" + string.Join(',', Enumerable.Repeat("0", 100_000)) + "]", "", TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => JsonPatch.Read(new string(' ', TextPatch.MaxCharacters + 1), "", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_JsonExpansionAndCombinedLimits_RejectsOversizedOutput()
    {
        var token = TestContext.Current.CancellationToken;
        var escaped = "\"" + new string('中', TextPatch.MaxCharacters / 5) + "\"";
        Assert.Throws<InvalidDataException>(() => JsonPatch.Apply("{}", "/value", escaped, false, false, "", true, token));
        var array = "[" + string.Join(',', Enumerable.Repeat("0", 50_000)) + "]";
        Assert.Throws<InvalidDataException>(() => JsonPatch.Apply("{\"existing\":" + array + "}", "/new", array, false, false, "", true, token));
        var nested = new string('[', 40) + "null" + new string(']', 40);
        Assert.Throws<InvalidOperationException>(() => JsonPatch.Apply(nested, string.Concat(Enumerable.Repeat("/0", 40)), nested, false, false, "", false, token));
    }

    [Fact]
    public void Apply_IniEdit_PreservesCommentsWhitespaceAndCase()
    {
        const string original = "; keep\r\nglobal=1\r\n[Service]\r\n  enabled = old  \r\npath=a;#literal\r\n[other]\r\nx=y";
        var result = IniPatch.Apply(original, "Service", "enabled", "new", false, true, "old", false, TestContext.Current.CancellationToken);
        Assert.Equal(original.Replace("= old", "= new", StringComparison.Ordinal), result.Text);
        Assert.Equal("a;#literal", IniPatch.Read(result.Text, "Service", "path", TestContext.Current.CancellationToken));
        Assert.Null(IniPatch.Read(result.Text, "service", "enabled", TestContext.Current.CancellationToken));
        Assert.False(IniPatch.Apply(result.Text, "Service", "enabled", "new", false, true, "old", false, TestContext.Current.CancellationToken).Changed);
        Assert.Throws<InvalidDataException>(() => IniPatch.Apply(original, "Service", "enabled", "new", false, true, "unexpected", false, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("", "new=1\n[first]\nx=1\n[last]\ny=2")]
    [InlineData("first", "[first]\nx=1\nnew=1\n[last]\ny=2")]
    [InlineData("last", "[first]\nx=1\n[last]\ny=2\nnew=1\n")]
    [InlineData("added", "[first]\nx=1\n[last]\ny=2\n[added]\nnew=1\n")]
    public void Apply_IniCreate_InsertsInCorrectSection(string section, string expected)
    {
        var result = IniPatch.Apply("[first]\nx=1\n[last]\ny=2", section, "new", "1", false, false, "", true, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Text);
        Assert.Equal("1", IniPatch.Read(result.Text, section, "new", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_IniDeleteAndEmptyValue_DistinguishesMissingFromEmpty()
    {
        const string original = "[s]\nx=\nkeep=1\n";
        Assert.Equal(string.Empty, IniPatch.Read(original, "s", "x", TestContext.Current.CancellationToken));
        var result = IniPatch.Apply(original, "s", "x", "", true, true, "", false, TestContext.Current.CancellationToken);
        Assert.Equal("[s]\nkeep=1\n", result.Text);
        Assert.Null(IniPatch.Read(result.Text, "s", "x", TestContext.Current.CancellationToken));
        Assert.False(IniPatch.Apply(result.Text, "s", "x", "", true, false, "", false, TestContext.Current.CancellationToken).Changed);
    }

    [Theory]
    [InlineData("x=1\nx=2")]
    [InlineData("[s]\nx=1\n[s]\ny=2")]
    [InlineData("[s] ; unsupported header comment\nx=1")]
    [InlineData("x:1")]
    [InlineData("x=1\rnext=2")]
    [InlineData("x=\0")]
    public void Read_AmbiguousOrUnsupportedIni_RejectsDocument(string text)
    {
        Assert.Throws<InvalidDataException>(() => IniPatch.Read(text, "", "x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_IniLineLimit_RejectsGrowthBeyondLimit()
    {
        var original = string.Concat(Enumerable.Repeat(";\n", 100_000));
        Assert.Throws<InvalidDataException>(() => IniPatch.Apply(original, "", "new", "1", false, false, "", true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_CancelledTransforms_StopBeforeProducingChanges()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => JsonPatch.Apply("{}", "/x", "1", false, false, "", true, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => IniPatch.Apply("", "", "x", "1", false, false, "", true, cancellation.Token));
    }
}