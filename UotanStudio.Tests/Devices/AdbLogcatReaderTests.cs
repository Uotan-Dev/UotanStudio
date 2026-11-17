using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class AdbLogcatReaderTests
{
    [Fact]
    public async Task RunAsync_LegacyDeviceRejectingYearFormat_RetriesWithThreadtimeOnly()
    {
        var commands = new List<string>();
        var reader = new AdbLogcatReader(async (command, onLine, token) =>
        {
            commands.Add(command);
            if (command is "ps -A -o PID,NAME")
            {
                onLine("123 shell");
                return;
            }

            if (command == AdbLogcatReader.LogcatCommand)
            {
                onLine("Invalid parameter to -v 'year'");
                onLine("Usage: logcat [options] [filterspecs]");
                return;
            }

            onLine("11-17 19:26:10.123  123  456 I Test: legacy output");
        });
        var entries = new List<AdbLogcatEntry>();

        await reader.RunAsync(entries.Add, TestContext.Current.CancellationToken);

        Assert.Equal(AdbLogcatReader.LegacyLogcatCommand, Assert.IsType<string>(commands.Last()));
        var raw = string.Concat(entries.Select(e => e.RawText));
        Assert.Contains("legacy output", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid parameter", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage: logcat", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ModernDevice_RunsOnlyThePreferredFormat()
    {
        var commands = new List<string>();
        var reader = new AdbLogcatReader((command, onLine, token) =>
        {
            commands.Add(command);
            if (command is "ps -A -o PID,NAME")
            {
                onLine("123 shell");
            }
            else
            {
                onLine("11-17 19:26:10.123  123  456 I Test: modern output");
            }
            return Task.CompletedTask;
        });
        var entries = new List<AdbLogcatEntry>();

        await reader.RunAsync(entries.Add, TestContext.Current.CancellationToken);

        Assert.Equal(["ps -A -o PID,NAME", AdbLogcatReader.LogcatCommand], commands);
        Assert.Contains("modern output", string.Concat(entries.Select(e => e.RawText)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Invalid parameter to -v 'year'", true)]
    [InlineData("invalid parameter TO -v: unrecognized format", true)]
    [InlineData("logcat: unrecognized option '-v year'", true)]
    [InlineData("11-17 19:26:10.123  123  456 I Test: -v", false)]
    [InlineData("Usage: logcat [options] [filterspecs]", false)]
    public void LooksLikeFormatRejection_DistinguishesErrorsFromLogLines(string line, bool expected)
    {
        Assert.Equal(expected, AdbLogcatReader.LooksLikeFormatRejection(line));
    }
}