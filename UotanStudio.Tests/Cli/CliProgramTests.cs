using UotanStudio.Cli;
using Xunit;

namespace UotanStudio.Tests.Cli;

public sealed class CliProgramTests
{
    [Theory]
    [InlineData(new[] { "--cli", "version" }, true)]
    [InlineData(new[] { "cli", "hdc", "targets" }, true)]
    [InlineData(new[] { "firmware.img" }, false)]
    [InlineData(new[] { "adb", "devices" }, true)]
    [InlineData(new string[] { }, false)]
    public void IsCommandLineMode_RecognizesSwitchAndKnownCommands(string[] args, bool expected)
    {
        Assert.Equal(expected, CliProgram.IsCommandLineMode(args));
    }

    [Fact]
    public async Task RunAsync_Help_DoesNotInitializeAvalonia()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await CliProgram.RunAsync(["--help"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("firmware", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(error.ToString());
    }
}
