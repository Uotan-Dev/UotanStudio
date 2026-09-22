using UotanStudio.Services.Patching;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchImageEditsTests
{
    [Fact]
    public void Append_Branches_DoNotChangeSharedInput()
    {
        var shared = PatchImageEdits.Empty.Append(new("/build.prop", "base", "utf8", ""));
        var first = shared.Append(new("/first", "one", "utf8", ""));
        var second = shared.Append(new("/second", "two", "utf8", ""));
        Assert.Single(shared.Items);
        Assert.Equal("/first", first.Items[1].Path);
        Assert.Equal("/second", second.Items[1].Path);
        Assert.Throws<NotSupportedException>(() => ((IList<PatchImageTextEdit>)shared.Items).Clear());
    }

    [Theory]
    [InlineData("build.prop")]
    [InlineData("/../other")]
    [InlineData("/a//b")]
    public void Append_DuplicateOrInvalidPath_RejectsWithoutChangingCollection(string path)
    {
        var edits = PatchImageEdits.Empty.Append(new("/build.prop", "", "utf8", ""));
        Assert.Throws<InvalidDataException>(() => edits.Append(new(path, "", "utf8", "")));
        Assert.Single(edits.Items);
    }

    [Fact]
    public void Append_ExceededCount_Rejects()
    {
        var edits = PatchImageEdits.Empty;
        for (var index = 0; index < 256; index++)
        {
            edits = edits.Append(new("/file" + index, "", "utf8", ""));
        }
        Assert.Throws<InvalidDataException>(() => edits.Append(new("/extra", "", "utf8", "")));
        Assert.Equal(256, edits.Items.Count);
    }
}