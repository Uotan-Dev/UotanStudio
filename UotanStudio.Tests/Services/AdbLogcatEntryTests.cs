using UotanStudio.Services;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class AdbLogcatEntryTests
{
    [Fact]
    public void Parse_PreservesAndroidStudioFieldsAndFullNames()
    {
        const string raw = "2026-09-07 13:01:14.491 4834 5700 D SystemUI:Keyguard: SensorChanged prox=0.0";
        const string processName = "com.example.application.with.a.long.name:remote";
        var entry = AdbLogcatEntry.Parse(raw, new Dictionary<int, string> { [4834] = processName });
        Assert.Equal(raw, entry.RawText);
        Assert.Equal("2026-09-07 13:01:14.491", entry.Timestamp);
        Assert.Equal(4834, entry.ProcessId);
        Assert.Equal(5700, entry.ThreadId);
        Assert.Equal("SystemUI:Keyguard", entry.Tag);
        Assert.Equal(processName, entry.ProcessName);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("SensorChanged prox=0.0", entry.Message);
        Assert.StartsWith(entry.Timestamp + " 4834-5700", entry.Text);
        Assert.Contains("SystemUI:Keyguard", entry.Text);
        Assert.Contains(processName + " D SensorChanged", entry.Text);
    }

    [Theory]
    [InlineData('V', LogLevel.Trace)]
    [InlineData('D', LogLevel.Debug)]
    [InlineData('I', LogLevel.Info)]
    [InlineData('W', LogLevel.Warn)]
    [InlineData('E', LogLevel.Error)]
    [InlineData('F', LogLevel.Fatal)]
    [InlineData('A', LogLevel.Fatal)]
    public void Parse_PreservesPrioritiesWithoutInventingUnknownMetadata(char priority, LogLevel level)
    {
        var entry = AdbLogcatEntry.Parse($"09-07 13:01:14.491123 4834 5700 {priority} Tag  : message");
        Assert.Equal(level, entry.Level);
        Assert.Equal(priority, entry.Priority);
        Assert.Equal("09-07 13:01:14.491123", entry.Timestamp);
        Assert.Equal("Tag", entry.Tag);
        Assert.Equal("-", entry.ProcessName);
        Assert.EndsWith(priority + " message", entry.Text);
    }

    [Theory]
    [InlineData("--------- beginning of main")]
    [InlineData("logcat: unexpected EOF!")]
    [InlineData("09-07 13:01:14.491 999999999999999 5700 D Tag: message")]
    public void Parse_PreservesUnrecognizedRows(string raw)
    {
        var entry = AdbLogcatEntry.Parse(raw);
        Assert.Null(entry.Level);
        Assert.Equal(raw, entry.Text);
    }

    [Fact]
    public void Parse_RetainsTheExistingLineLengthLimit()
    {
        var entry = AdbLogcatEntry.Parse("2026-09-07 13:01:14.491 4834 5700 D Tag: " + new string('x', 20000));
        Assert.Equal(16384, entry.RawText.Length);
        Assert.Equal(LogLevel.Debug, entry.Level);
    }
}
