using System.Xml;
using UotanStudio.Firmware.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ConfigurationPatchTests
{
    [Theory]
    [InlineData(TextPatchOperation.Replace, "a\r\nnew\r\nz")]
    [InlineData(TextPatchOperation.InsertBefore, "a\r\nnewold\r\nz")]
    [InlineData(TextPatchOperation.InsertAfter, "a\r\noldnew\r\nz")]
    [InlineData(TextPatchOperation.Delete, "a\r\n\r\nz")]
    public void Apply_AnchoredText_PreservesUnrelatedNewlines(TextPatchOperation operation, string expected)
    {
        var result = TextPatch.Apply("a\r\nold\r\nz", "old", "new", operation, false, 1, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Text);
        Assert.Equal(1, result.Matches);
        Assert.True(result.Changed);
    }

    [Theory]
    [InlineData("missing", "old", 1)]
    [InlineData("old old", "old", 1)]
    [InlineData("old", "^", 1)]
    public void Apply_UnexpectedOrEmptyMatches_RejectsPatch(string text, string find, int expected)
    {
        Assert.Throws<InvalidDataException>(() => TextPatch.Apply(text, find, "new", TextPatchOperation.Replace, true, expected, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_RegexReplacement_TreatsDollarAndBackslashLiterally()
    {
        var result = TextPatch.Apply("foo12", "foo[0-9]+", "$1\\path", TextPatchOperation.Replace, true, 1, TestContext.Current.CancellationToken);
        Assert.Equal("$1\\path", result.Text);
    }

    [Fact]
    public void Apply_ExpansionBeyondLimit_RejectsBeforeReturningOutput()
    {
        var replacement = new string('x', TextPatch.MaxCharacters);
        Assert.Throws<InvalidDataException>(() => TextPatch.Apply("aa", "a", replacement, TextPatchOperation.Replace, false, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Apply_CancelledToken_DoesNotReturnPatchedText()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TextPatch.Apply("old", "old", "new", TextPatchOperation.Replace, false, 1, cancellation.Token));
    }

    [Fact]
    public void Apply_PropertyOldValue_PreservesCommentsSpacingAndFinalNewline()
    {
        const string input = "# keep\r\n ro.test = old  \r\nother=value";
        var result = AndroidPropertyPatch.Apply(input, "ro.test", "new", false, true, "old", false);
        Assert.Equal("# keep\r\n ro.test = new  \r\nother=value", result.Text);
        var repeated = AndroidPropertyPatch.Apply(result.Text, "ro.test", "new", false, true, "old", false);
        Assert.False(repeated.Changed);
        Assert.Equal(result.Text, repeated.Text);
    }

    [Theory]
    [InlineData("ro.test=a\nro.test=b", "a")]
    [InlineData("ro.test=unexpected", "a")]
    [InlineData("other=a", "a")]
    public void Apply_AmbiguousOrConflictingProperty_RejectsPatch(string text, string oldValue)
    {
        Assert.Throws<InvalidDataException>(() => AndroidPropertyPatch.Apply(text, "ro.test", "new", false, true, oldValue, false));
    }

    [Fact]
    public void Apply_PropertyCreateAndDelete_PreservesOtherProperties()
    {
        var added = AndroidPropertyPatch.Apply("other=a", "ro.test", "new", false, false, "", true);
        Assert.Equal("other=a\nro.test=new\n", added.Text);
        var deleted = AndroidPropertyPatch.Apply(added.Text, "ro.test", "", true, true, "new", false);
        Assert.Equal("other=a\n", deleted.Text);
        Assert.False(AndroidPropertyPatch.Apply(deleted.Text, "ro.test", "", true, false, "", false).Changed);
    }

    [Fact]
    public void Apply_XmlNamespaceAttribute_ChangesOnlySelectedValue()
    {
        const string input = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<root xmlns:a=\"urn:test\">\r\n  <!-- keep -->\r\n  <a:item name=\"old\" />\r\n</root>";
        var result = XmlPatch.Apply(input, "/root/a:item/@name", "a=urn:test", XmlPatchOperation.SetValue, "new", "", 1, true, "old");
        Assert.Equal(input.Replace("name=\"old\"", "name=\"new\"", StringComparison.Ordinal), result.Text);
        Assert.Equal("new", Assert.Single(XmlPatch.Query(result.Text, "/root/a:item/@name", "a=urn:test", 1)));
        Assert.False(XmlPatch.Apply(result.Text, "/root/a:item/@name", "a=urn:test", XmlPatchOperation.SetValue, "new", "", 1, true, "old").Changed);
    }

    [Fact]
    public void Apply_XmlAppendRepeated_DoesNotDuplicateElement()
    {
        var first = XmlPatch.Apply("<root />", "/root", "", XmlPatchOperation.AppendElement, "<item name=\"test\" />", "", 1, false, "");
        var second = XmlPatch.Apply(first.Text, "/root", "", XmlPatchOperation.AppendElement, "<item name=\"test\" />", "", 1, false, "");
        Assert.False(second.Changed);
        Assert.Single(XmlPatch.Query(second.Text, "/root/item", "", 1));
    }

    [Fact]
    public void Query_ExternalEntity_RejectsDocument()
    {
        Assert.Throws<XmlException>(() => XmlPatch.Query("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///secret'>]><root>&x;</root>", "/root", "", 1));
    }

    [Fact]
    public void Apply_XmlParentValueReplacement_RejectsImplicitChildDeletion()
    {
        Assert.Throws<InvalidDataException>(() => XmlPatch.Apply("<root><child /></root>", "/root", "", XmlPatchOperation.SetValue, "new", "", 1, false, ""));
    }
}