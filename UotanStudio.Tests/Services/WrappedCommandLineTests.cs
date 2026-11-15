using UotanStudio.Services.Terminal.Wrapped;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WrappedCommandLineTests
{
    [Theory]
    [InlineData("adb", WrappedTool.Adb)]
    [InlineData("ADB.EXE", WrappedTool.Adb)]
    [InlineData("fastboot", WrappedTool.Fastboot)]
    [InlineData("Fastboot.exe", WrappedTool.Fastboot)]
    [InlineData("hdc", WrappedTool.Hdc)]
    [InlineData("git", null)]
    [InlineData(".\\adb", null)]
    internal void DetectTool_KnownTokens_MapsToTool(string token, WrappedTool? expected)
    {
        Assert.Equal(expected, WrappedCommandLine.DetectTool(token));
    }

    [Fact]
    public void Tokenize_PlainLine_SplitsOnWhitespace()
    {
        Assert.Equal(["adb", "devices"], WrappedCommandLine.Tokenize("  adb   devices ")!);
    }

    [Fact]
    public void Tokenize_WindowsPath_KeepsBackslashes()
    {
        Assert.Equal(["adb", "push", @"C:\Users\me\image.bin", "/data/local/tmp/"],
            WrappedCommandLine.Tokenize("adb push C:\\Users\\me\\image.bin /data/local/tmp/")!);
    }

    [Fact]
    public void Tokenize_QuotedPath_PreservesSpaces()
    {
        Assert.Equal(["adb", "push", @"D:\my files\a b.apk", "/sdcard/"],
            WrappedCommandLine.Tokenize("adb push \"D:\\my files\\a b.apk\" /sdcard/")!);
    }

    [Fact]
    public void Tokenize_SingleQuotes_GroupsLiterally()
    {
        Assert.Equal(["hdc", "shell", "echo a b"], WrappedCommandLine.Tokenize("hdc shell 'echo a b'")!);
    }

    [Fact]
    public void Tokenize_UnterminatedQuote_ReturnsNull()
    {
        Assert.Null(WrappedCommandLine.Tokenize("adb push \"a b"));
    }

    [Theory]
    [InlineData("adb push *.apk /sdcard/")]
    [InlineData("adb shell ls | grep init")]
    [InlineData("adb devices & fastboot devices")]
    [InlineData("hdc shell echo $HOME")]
    [InlineData("adb devices > out.txt")]
    public void TryParse_ShellMetacharacters_ReturnsNull(string line)
    {
        Assert.Null(WrappedCommandLine.TryParse(line));
    }

    [Fact]
    public void TryParse_MetacharacterInsideQuotes_StillParses()
    {
        var command = WrappedCommandLine.TryParse("adb shell \"ls | grep init\"");
        Assert.NotNull(command);
        Assert.Equal("shell", command!.Command);
        Assert.Equal(["ls | grep init"], command.Arguments);
    }

    [Fact]
    public void TryParse_AdbDevices_ParsesSubcommand()
    {
        var command = WrappedCommandLine.TryParse("adb devices");
        Assert.NotNull(command);
        Assert.Equal(WrappedTool.Adb, command!.Tool);
        Assert.Equal("devices", command.Command);
        Assert.Empty(command.Arguments);
        Assert.Null(command.Target);
    }

    [Fact]
    public void TryParse_AdbSerialOption_ExtractsTarget()
    {
        var command = WrappedCommandLine.TryParse("adb -s R58M1234 shell dumpsys battery");
        Assert.NotNull(command);
        Assert.Equal("R58M1234", command!.Target);
        Assert.Equal("shell", command.Command);
        Assert.Equal(["dumpsys", "battery"], command.Arguments);
    }

    [Fact]
    public void TryParse_AdbDeviceFlags_SetSelection()
    {
        var command = WrappedCommandLine.TryParse("adb -d shell ls");
        Assert.NotNull(command);
        Assert.True(command!.UseUsb);
        Assert.False(command.UseTcp);
        Assert.Equal("shell", command.Command);
    }

    [Fact]
    public void TryParse_HdcTargetOption_ExtractsConnectKey()
    {
        var command = WrappedCommandLine.TryParse("hdc -t 127.0.0.1:5555 file send local remote");
        Assert.NotNull(command);
        Assert.Equal(WrappedTool.Hdc, command!.Tool);
        Assert.Equal("127.0.0.1:5555", command.Target);
        Assert.Equal("file", command.Command);
        Assert.Equal(["send", "local", "remote"], command.Arguments);
    }

    [Fact]
    public void TryParse_FastbootSerialOption_ExtractsTarget()
    {
        var command = WrappedCommandLine.TryParse("fastboot -s ABC123 getvar product");
        Assert.NotNull(command);
        Assert.Equal(WrappedTool.Fastboot, command!.Tool);
        Assert.Equal("ABC123", command.Target);
        Assert.Equal("getvar", command.Command);
        Assert.Equal(["product"], command.Arguments);
    }

    [Theory]
    [InlineData("adb", "help")]
    [InlineData("adb -h", "help")]
    [InlineData("adb --help", "help")]
    [InlineData("adb --version", "version")]
    [InlineData("fastboot --version", "version")]
    [InlineData("hdc -v", "version")]
    [InlineData("hdc help", "help")]
    public void TryParse_BareToolAndFlags_NormalizeToCanonicalCommands(string line, string expected)
    {
        Assert.Equal(expected, WrappedCommandLine.TryParse(line)!.Command);
    }

    [Fact]
    public void TryParse_HdcVersionFlag_NormalizesToVersion()
    {
        Assert.Equal("version", WrappedCommandLine.TryParse("hdc -v")!.Command);
    }

    [Fact]
    public void TryParse_NonToolLine_ReturnsNull()
    {
        Assert.Null(WrappedCommandLine.TryParse("git status"));
        Assert.Null(WrappedCommandLine.TryParse(""));
        Assert.Null(WrappedCommandLine.TryParse("   "));
    }

    [Fact]
    public void TryParse_UnknownAdbSubcommand_StillParsesForCallerDecision()
    {
        var command = WrappedCommandLine.TryParse("adb frobnicate");
        Assert.NotNull(command);
        Assert.Equal("frobnicate", command!.Command);
    }
}