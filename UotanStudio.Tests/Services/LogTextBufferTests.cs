using AvaloniaEdit.Document;
using UotanStudio.Services;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Checks multiline log eviction against text anchors and per-line severity.</summary>
public sealed class LogTextBufferTests
{
    [Fact]
    public void Eviction_PreservesRemainingTextAnchorAndLineLevels()
    {
        var document = new TextDocument();
        var buffer = new LogTextBuffer(document);
        buffer.Insert(0, new[] { (Text: "old\ntrace", Level: LogLevel.Debug), (Text: "keep", Level: LogLevel.Error) },
            static entry => entry.Text, static entry => entry.Level);
        var anchor = document.CreateAnchor(document.Text.IndexOf("keep", StringComparison.Ordinal));
        buffer.Remove(0, 1);
        Assert.Equal("keep\n", document.Text);
        Assert.False(anchor.IsDeleted);
        Assert.Equal(0, anchor.Offset);
        Assert.Equal([LogLevel.Error], buffer.Levels);
        buffer.Insert(0, new[] { (Text: "before", Level: LogLevel.Info) }, static entry => entry.Text, static entry => entry.Level);
        Assert.Equal("before\nkeep\n", document.Text);
        Assert.Equal([LogLevel.Info, LogLevel.Error], buffer.Levels);
    }

    [Fact]
    public void Reset_DiscardsOldEntryBoundariesBeforeNewMultilineData()
    {
        var document = new TextDocument();
        var buffer = new LogTextBuffer(document);
        buffer.Insert(0, new[] { "old", "old2" }, static entry => entry, static _ => LogLevel.Warn);
        buffer.Clear();
        buffer.Insert(0, new[] { "new\nline", "tail" }, static entry => entry, static _ => LogLevel.Info);
        buffer.Remove(1, 1);
        Assert.Equal("new\nline\n", document.Text);
        Assert.Equal([LogLevel.Info, LogLevel.Info], buffer.Levels);
    }
}