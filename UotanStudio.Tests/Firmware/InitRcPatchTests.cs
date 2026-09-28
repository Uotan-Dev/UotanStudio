using UotanStudio.Firmware.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class InitRcPatchTests
{
    [Theory]
    [InlineData("a b", "a\\ b")]
    [InlineData("# comment", "\\#\\ comment")]
    [InlineData("a\"b\\c", "a\\\"b\\\\c")]
    [InlineData("a\nb\r\t", "a\\nb\\r\\t")]
    [InlineData("", "\"\"")]
    public void EditArgument_DecodedValue_EncodesOneTokenAndPreservesOtherSource(string value, string encoded)
    {
        const string text = "on boot\r\n\tsetprop  \"key\"\t\"old value\" # keep\r\n";
        var result = InitRcPatch.EditArgument(text, InitRcSection.Action, "boot", "setprop", 1,
            value, true, "old value", 1, 1, TestContext.Current.CancellationToken);
        Assert.Equal("on boot\r\n\tsetprop  \"key\"\t" + encoded + " # keep\r\n", result.Text);
        var repeat = InitRcPatch.EditArgument(result.Text, InitRcSection.Action, "boot", "setprop", 1,
            value, true, value, 1, 1, TestContext.Current.CancellationToken);
        Assert.False(repeat.Changed);
    }

    [Fact]
    public void EditArgument_HeaderExecutable_PreservesOtherArgumentsAndRejectsRenameCollision()
    {
        const string text = "service test  /old  \"keep arg\" # keep\n user root\nservice other /other\n";
        var result = InitRcPatch.EditArgument(text, InitRcSection.Service, "test", "", 1, "/new", true, "/old",
            1, 1, TestContext.Current.CancellationToken);
        Assert.Equal(text.Replace("/old", "/new", StringComparison.Ordinal), result.Text);
        Assert.Throws<InvalidDataException>(() => InitRcPatch.EditArgument(text, InitRcSection.Service,
            "test", "", 0, "other", false, "", 1, 1, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1, false, "")]
    [InlineData(2, false, "")]
    [InlineData(1, true, "wrong")]
    public void EditArgument_InvalidIndexOrOldValue_Rejects(int index, bool guard, string expected)
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.EditArgument("on boot\nsetprop key old", InitRcSection.Action,
            "boot", "setprop", index, "new", guard, expected, 1, 1, TestContext.Current.CancellationToken));
    [Theory]
    [InlineData(InitRcInsertion.BeforeAnchor)]
    [InlineData(InitRcInsertion.AfterAnchor)]
    public void Add_AnchoredPlacement_PreservesContinuationAndRepeats(InitRcInsertion placement)
    {
        const string prefix = "on boot\r\n# retain\r\n";
        const string anchor = "    start \\\r\n        target # keep\r\n";
        const string added = "    setprop ready 1\r\n";
        const string suffix = "    start other\r\n";
        var token = TestContext.Current.CancellationToken;
        var result = InitRcPatch.Add(prefix + anchor + suffix, InitRcSection.Action, "boot", "setprop ready 1",
            1, 1, token, placement, "start target");
        Assert.Equal(prefix + (placement == InitRcInsertion.BeforeAnchor ? added + anchor : anchor + added) + suffix, result.Text);
        var repeat = InitRcPatch.Add(result.Text, InitRcSection.Action, "boot", "setprop ready 1",
            1, 1, token, placement, "start target");
        Assert.False(repeat.Changed);
        Assert.Equal(1, repeat.Matches);
    }

    [Theory]
    [InlineData("on boot\nstart target\nstart target")]
    [InlineData("on boot\nstart other")]
    [InlineData("on boot\nsetprop ready 1\nstart other\nstart target")]
    public void Add_MissingDuplicateAnchorOrWrongExistingPosition_Rejects(string text)
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.Add(text, InitRcSection.Action, "boot", "setprop ready 1",
            1, 1, TestContext.Current.CancellationToken, InitRcInsertion.BeforeAnchor, "start target"));

    [Fact]
    public void Add_AfterAnchorWithoutFinalNewline_AddsSeparator()
        => Assert.Equal("on boot\nstart target\n    setprop ready 1\n", InitRcPatch.Add("on boot\nstart target",
            InitRcSection.Action, "boot", "setprop ready 1", 1, 1, TestContext.Current.CancellationToken,
            InitRcInsertion.AfterAnchor, "start target").Text);
    [Theory]
    [InlineData(InitRcSection.Service, "test", "service test /old", "service test /new --flag")]
    [InlineData(InitRcSection.Action, "boot", "on boot", "on property:ready=1 && boot")]
    [InlineData(InitRcSection.Import, "/old", "import /old", "import /new")]
    public void ReplaceHeader_AllKinds_PreservesSurroundingSource(InitRcSection kind, string selector, string before, string after)
    {
        var body = kind == InitRcSection.Import ? "" : "\t" + (kind == InitRcSection.Service ? "user root" : "start test") + "\r\n";
        var text = "# keep\r\n " + before + " # retained\r\n" + body;
        var result = InitRcPatch.ReplaceHeader(text, kind, selector, after, true, before, TestContext.Current.CancellationToken);
        Assert.Equal("# keep\r\n " + after + " # retained\r\n" + body, result.Text);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Matches);
    }

    [Theory]
    [InlineData("service test /old\nservice other /other", "service other /new")]
    [InlineData("service test /old", "on boot")]
    [InlineData("service test /old", "service test /new\nuser root")]
    [InlineData("service test /old", "service test /new # injected")]
    [InlineData("service test /old\nservice test /old", "service test /new")]
    public void ReplaceHeader_ConflictOrInvalidReplacement_Rejects(string text, string replacement)
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.ReplaceHeader(text, InitRcSection.Service,
            "test", replacement, false, "", TestContext.Current.CancellationToken));

    [Fact]
    public void ReplaceHeader_OldValueMismatch_Rejects()
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.ReplaceHeader("service test /old", InitRcSection.Service,
            "test", "service test /new", true, "service test /different", TestContext.Current.CancellationToken));

    [Fact]
    public void ReplaceHeader_UnchangedHeader_IsNoOp()
    {
        var result = InitRcPatch.ReplaceHeader("service test /old\n user root", InitRcSection.Service,
            "test", "service test /old", false, "", TestContext.Current.CancellationToken);
        Assert.False(result.Changed);
    }
    [Theory]
    [InlineData("service test /vendor/bin/test\n    user root")]
    [InlineData("on boot\n    setprop ready 1")]
    [InlineData("import /vendor/etc/init/test.rc")]
    public void AddSection_MissingThenRepeated_AppendsOnce(string section)
    {
        var token = TestContext.Current.CancellationToken;
        var result = InitRcPatch.AddSection("# existing\r\n", section, token);
        Assert.Equal("# existing\r\n" + section + "\r\n", result.Text);
        Assert.True(result.Changed);
        Assert.Equal(0, result.Matches);
        var repeated = InitRcPatch.AddSection(result.Text, section, token);
        Assert.Equal(result.Text, repeated.Text);
        Assert.False(repeated.Changed);
        Assert.Equal(1, repeated.Matches);
    }

    [Fact]
    public void AddSection_EquivalentTokens_PreservesExistingFormatting()
    {
        const string text = "service \"test\" /test\r\n\tuser root # keep\r\n";
        var result = InitRcPatch.AddSection(text, "service test /test\n    user \"root\"\n", TestContext.Current.CancellationToken);
        Assert.Equal(text, result.Text);
        Assert.False(result.Changed);
    }

    [Theory]
    [InlineData("service test /other\n user root", "service test /test\n user root")]
    [InlineData("on boot\n start a\n start b", "on boot\n start b\n start a")]
    [InlineData("service test /test\nservice test /test", "service test /test")]
    [InlineData("", "service test /test\nservice second /second")]
    [InlineData("", "import /test\nuser root")]
    [InlineData("", "# no section")]
    public void AddSection_ConflictOrInvalidFragment_Rejects(string text, string section)
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.AddSection(text, section, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("", "import /test\n")]
    [InlineData("# comment", "# comment\nimport /test\n")]
    public void AddSection_EmptyOrUnterminatedTarget_SeparatesHeader(string text, string expected)
        => Assert.Equal(expected, InitRcPatch.AddSection(text, "import /test", TestContext.Current.CancellationToken).Text);

    [Fact]
    public void AddSection_QuotedNewline_PreservesFragmentContent()
    {
        const string section = "on boot\n    setprop multiline \"a\nb\"";
        Assert.Equal("# target\r\n" + section + "\r\n", InitRcPatch.AddSection("# target\r\n", section,
            TestContext.Current.CancellationToken).Text);
    }
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Add_MissingOption_PreservesCommentsAndIsRepeatable(string newline)
    {
        var text = $"service test /test{newline}    user root{newline}# next{newline}on boot{newline}    start test";
        var result = InitRcPatch.Add(text, InitRcSection.Service, "test", "group root shell", 0, 1, TestContext.Current.CancellationToken);
        Assert.Equal(text.Replace("# next", $"    group root shell{newline}# next", StringComparison.Ordinal), result.Text);
        Assert.True(result.Changed);
        var repeated = InitRcPatch.Add(result.Text, InitRcSection.Service, "test", "group root shell", 0, 1, TestContext.Current.CancellationToken);
        Assert.Equal(result.Text, repeated.Text);
        Assert.False(repeated.Changed);
        Assert.Equal(1, repeated.Matches);
    }

    [Fact]
    public void Add_ActionIdentity_AllowsDifferentPropertiesAndRejectsConflict()
    {
        const string text = "on boot\n    setprop a 1";
        var result = InitRcPatch.Add(text, InitRcSection.Action, "boot", "setprop b 2", 1, 1, TestContext.Current.CancellationToken);
        Assert.Equal(text + "\n    setprop b 2\n", result.Text);
        Assert.Throws<InvalidDataException>(() => InitRcPatch.Add(result.Text, InitRcSection.Action,
            "boot", "setprop b 3", 1, 1, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("service test /test\n user root\n user root", "user root")]
    [InlineData("service test /test", "user root\nservice injected /bin/test")]
    public void Add_DuplicateIdentityOrSectionInjection_Rejects(string text, string statement)
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.Add(text, InitRcSection.Service,
            "test", statement, 0, 1, TestContext.Current.CancellationToken));

    [Fact]
    public void Add_EquivalentQuotedTokens_PreservesOriginalBytes()
    {
        const string text = "service test /test\r\n\tuser \"root\" # keep";
        Assert.Equal(text, InitRcPatch.Add(text, InitRcSection.Service, "test", "user root", 0, 1,
            TestContext.Current.CancellationToken).Text);
    }
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Apply_ServiceDirective_PreservesOtherSectionsAndLayout(string newline)
    {
        var original = $"# keep{newline}service vendor.test /vendor/bin/test{newline}\tgroup root shell # retained{newline}on boot{newline}    start vendor.test{newline}";
        var result = InitRcPatch.Apply(original, InitRcSection.Service, "vendor.test", "group",
            InitRcOperation.Replace, "group system shell", true, "group root shell", 1, 1, TestContext.Current.CancellationToken);
        Assert.Equal(original.Replace("group root shell", "group system shell", StringComparison.Ordinal), result.Text);
        Assert.True(result.Changed);
        var repeated = InitRcPatch.Apply(result.Text, InitRcSection.Service, "vendor.test", "group",
            InitRcOperation.Replace, "group system shell", false, "", 1, 1, TestContext.Current.CancellationToken);
        Assert.False(repeated.Changed);
    }

    [Fact]
    public void Read_QuotesCommentsContinuationsAndUnindentedBody_FollowsInitTokens()
    {
        const string text = "service \"vendor.test\" /vendor/bin/test\nuser root\n    group root \\\r\n        shell # comment\non property:ready=1 && boot\n    setprop value a#b\nimport /vendor/etc/init/${ro.hardware}.rc\n";
        var token = TestContext.Current.CancellationToken;
        Assert.Equal("group root \\\r\n        shell", Assert.Single(InitRcPatch.Read(text, InitRcSection.Service,
            "vendor.test", "group", 1, 1, token)));
        Assert.Equal("setprop value a#b", Assert.Single(InitRcPatch.Read(text, InitRcSection.Action,
            "property:ready=1 && boot", "setprop", 1, 1, token)));
        Assert.Equal("import /vendor/etc/init/${ro.hardware}.rc", Assert.Single(InitRcPatch.Read(text, InitRcSection.Import,
            "/vendor/etc/init/${ro.hardware}.rc", "", 1, 1, token)));
    }

    [Fact]
    public void Apply_DeleteContinuation_RemovesEntireStatementAndRetainsAdjacentComment()
    {
        const string text = "service test /test\n# retain\n    group root \\\n shell # remove\n    user root";
        var result = InitRcPatch.Apply(text, InitRcSection.Service, "test", "group", InitRcOperation.Delete,
            "", false, "", 1, 1, TestContext.Current.CancellationToken);
        Assert.Equal("service test /test\n# retain\n    user root", result.Text);
    }

    [Theory]
    [InlineData("service test /test\n    user root\nservice test /other\n    user root\n")]
    [InlineData("service test /test\n    user root\n    user system\n")]
    [InlineData("service test /test\n    user \"unterminated\n")]
    [InlineData("user root\n")]
    [InlineData("import /test\nuser root\n")]
    [InlineData("service test\n")]
    [InlineData("service test /test\nuser root\0\n")]
    public void Read_AmbiguousOrMalformedInput_Rejects(string text)
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.Read(text, InitRcSection.Service,
            "test", "user", 1, 1, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("user system\nservice injected /bin/test")]
    [InlineData("user system # comment")]
    [InlineData("group system")]
    [InlineData("user system\\")]
    public void Apply_InvalidReplacement_Rejects(string replacement)
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.Apply("service test /test\n user root\n",
            InitRcSection.Service, "test", "user", InitRcOperation.Replace, replacement,
            false, "", 1, 1, TestContext.Current.CancellationToken));

    [Fact]
    public void Apply_OldValueMismatch_Rejects()
        => Assert.Throws<InvalidDataException>(() => InitRcPatch.Apply("service test /test\n user root\n",
            InitRcSection.Service, "test", "user", InitRcOperation.Replace, "user system",
            true, "user shell", 1, 1, TestContext.Current.CancellationToken));

    [Fact]
    public void Read_CancelledEmptyInput_Throws()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => InitRcPatch.Read("", InitRcSection.Service,
            "test", "", 0, 0, source.Token));
    }

    [Theory]
    [InlineData("service te\\st /test\nuser root", "test")]
    [InlineData("service \"test\" /test\nuser root", "test")]
    [InlineData("service te\\\nst /test\nuser root", "test")]
    [InlineData("service test#name /test\nuser root", "test#name")]
    public void Read_EncodedServiceName_MatchesDecodedToken(string text, string selector)
        => Assert.Equal("user root", Assert.Single(InitRcPatch.Read(text, InitRcSection.Service,
            selector, "user", 1, 1, TestContext.Current.CancellationToken)));

    [Fact]
    public void Read_ExceededTokenAndLineLimits_Rejects()
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Throws<InvalidDataException>(() => InitRcPatch.Read("service test /" + new string('a', 16384),
            InitRcSection.Service, "test", "", 1, 1, token));
        Assert.Throws<InvalidDataException>(() => InitRcPatch.Read(new string('\n', 100000),
            InitRcSection.Service, "test", "", 0, 0, token));
    }
}