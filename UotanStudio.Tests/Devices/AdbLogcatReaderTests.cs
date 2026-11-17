using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class AdbLogcatReaderTests
{
    [Fact]
    public async Task RunAsync_Android44WithoutAllBucket_FallsBackToDefaultBucket()
    {
        var commands = new List<string>();
        var reader = new AdbLogcatReader((command, onLine, token) =>
        {
            commands.Add(command);
            if (command is "ps -A -o PID,NAME")
            {
                onLine("123 shell");
                return Task.CompletedTask;
            }

            if (command != AdbLogcatReader.DefaultBucketLogcatCommand)
            {
                onLine("Unable to open log device '/dev/log/all': No such file or directory");
                return Task.CompletedTask;
            }

            onLine("--------- beginning of /dev/log/main");
            onLine("11-17 23:58:08.176  1188  1188 D wpa_supplicant: ongoing scan");
            return Task.CompletedTask;
        });
        var entries = new List<AdbLogcatEntry>();

        await reader.RunAsync(entries.Add, TestContext.Current.CancellationToken);

        Assert.Equal(AdbLogcatReader.DefaultBucketLogcatCommand, commands.Last());
        var raw = string.Concat(entries.Select(e => e.RawText));
        Assert.Contains("ongoing scan", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Unable to open log device", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Android44RejectingYearFormat_WalksTheWholeLadder()
    {
        var commands = new List<string>();
        var reader = new AdbLogcatReader((command, onLine, token) =>
        {
            commands.Add(command);
            switch (command)
            {
                case "ps -A -o PID,NAME":
                    onLine("123 shell");
                    break;
                case AdbLogcatReader.LogcatCommand:
                    onLine("Invalid parameter to -v");
                    onLine("Usage: logcat [options] [filterspecs]");
                    break;
                case AdbLogcatReader.LegacyLogcatCommand:
                    onLine("Unable to open log device '/dev/log/all': No such file or directory");
                    break;
                default:
                    onLine("11-17 23:58:08.176  1188  1188 D wpa_supplicant: streaming");
                    break;
            }
            return Task.CompletedTask;
        });
        var entries = new List<AdbLogcatEntry>();

        await reader.RunAsync(entries.Add, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["ps -A -o PID,NAME", .. AdbLogcatReader.CommandLadder],
            commands);
        var raw = string.Concat(entries.Select(e => e.RawText));
        Assert.Contains("streaming", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid parameter", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Unable to open log device", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage: logcat", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ModernDevice_RunsOnlyThePreferredCommand()
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
                onLine("11-17 23:58:08.176  1188  1188 D wpa_supplicant: modern");
            }
            return Task.CompletedTask;
        });
        var entries = new List<AdbLogcatEntry>();

        await reader.RunAsync(entries.Add, TestContext.Current.CancellationToken);

        Assert.Equal(["ps -A -o PID,NAME", AdbLogcatReader.LogcatCommand], commands);
        Assert.Contains("modern", string.Concat(entries.Select(e => e.RawText)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ConnectedDeviceThatStaysSilent_DoesNotWalkTheLadder()
    {
        var commands = new List<string>();
        var reader = new AdbLogcatReader((command, onLine, token) =>
        {
            commands.Add(command);
            return Task.CompletedTask;
        });

        await reader.RunAsync(_ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(["ps -A -o PID,NAME", AdbLogcatReader.LogcatCommand], commands);
    }

    [Theory]
    [InlineData("Invalid parameter to -v 'year'", true)]
    [InlineData("Unable to open log device '/dev/log/all': No such file or directory", true)]
    [InlineData("logcat: Unknown option -b", true)]
    [InlineData("11-17 23:58:08.176  1188  1188 D wpa_supplicant: -v", false)]
    [InlineData("--------- beginning of /dev/log/main", false)]
    public void LooksLikeRejection_DistinguishesRefusalsFromLogLines(string line, bool expected)
    {
        Assert.Equal(expected, AdbLogcatReader.LooksLikeRejection(line));
    }

    [Fact]
    public void CommandLadder_OrdersNewestFormatFirstAndDefaultBucketLast()
    {
        Assert.Equal(AdbLogcatReader.LogcatCommand, AdbLogcatReader.CommandLadder[0]);
        Assert.Equal(AdbLogcatReader.DefaultBucketLogcatCommand, AdbLogcatReader.CommandLadder[^1]);
        Assert.Equal(3, AdbLogcatReader.CommandLadder.Count);
    }
}