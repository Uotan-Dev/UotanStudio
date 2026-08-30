using UotanStudio.Services.Cli;
using Xunit;

namespace UotanStudio.Tests.Cli;

/// <summary>
/// Unit tests for <see cref="FirmwareCliParser"/>: verb dispatch, flag parsing, and error paths.
/// <para><see cref="FirmwareCliParser"/> 单元测试：子命令分发、标志解析与错误路径。</para>
/// </summary>
public class FirmwareCliParserTests
{
    [Fact]
    public void EmptyArgs_ReturnsListWithHelpRequested()
    {
        var result = FirmwareCliParser.Parse([]);

        Assert.False(result.IsError);
        Assert.Equal(FirmwareCliVerb.List, result.Command!.Verb);
        Assert.True(result.Command!.HelpRequested);
    }

    [Fact]
    public void NullArgs_ReturnsListWithHelpRequested()
    {
        var result = FirmwareCliParser.Parse(null);

        Assert.False(result.IsError);
        Assert.True(result.Command!.HelpRequested);
    }

    [Fact]
    public void HelpFlag_AsFirstToken_ReturnsHelpRequested()
    {
        var result = FirmwareCliParser.Parse(["--help"]);

        Assert.False(result.IsError);
        Assert.True(result.Command!.HelpRequested);
    }

    [Fact]
    public void ShortHelpFlag_ReturnsHelpRequested()
    {
        var result = FirmwareCliParser.Parse(["-h"]);

        Assert.True(result.Command!.HelpRequested);
    }

    [Fact]
    public void VersionFlag_AsFirstToken_ReturnsVersionRequested()
    {
        var result = FirmwareCliParser.Parse(["--version"]);

        Assert.False(result.IsError);
        Assert.True(result.Command!.VersionRequested);
    }

    [Fact]
    public void List_ParsesSuccessfully()
    {
        var result = FirmwareCliParser.Parse(["list"]);

        Assert.False(result.IsError);
        Assert.Equal(FirmwareCliVerb.List, result.Command!.Verb);
    }

    [Fact]
    public void List_WithUnknownFlag_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["list", "--bogus"]);

        Assert.True(result.IsError);
        Assert.Contains("Unknown flag", result.Error!);
    }

    [Fact]
    public void Info_WithFilePath_ParsesSuccessfully()
    {
        var result = FirmwareCliParser.Parse(["info", "boot.img"]);

        Assert.False(result.IsError);
        Assert.Equal(FirmwareCliVerb.Info, result.Command!.Verb);
        Assert.Equal("boot.img", result.Command!.InputPath);
    }

    [Fact]
    public void Info_WithoutFilePath_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["info"]);

        Assert.True(result.IsError);
        Assert.Contains("requires a file path", result.Error!);
    }

    [Fact]
    public void Info_WithExtraPath_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["info", "a.img", "b.img"]);

        Assert.True(result.IsError);
        Assert.Contains("takes exactly one file path", result.Error!);
    }

    [Fact]
    public void Info_RejectsFormatFlag()
    {
        var result = FirmwareCliParser.Parse(["info", "boot.img", "--format", "ext4"]);

        Assert.True(result.IsError);
        Assert.Contains("does not accept --format", result.Error!);
    }

    [Fact]
    public void Unpack_WithInputAndOutput_ParsesSuccessfully()
    {
        var result = FirmwareCliParser.Parse(["unpack", "boot.img", "-o", "out/"]);

        Assert.False(result.IsError);
        Assert.Equal(FirmwareCliVerb.Unpack, result.Command!.Verb);
        Assert.Equal("boot.img", result.Command!.InputPath);
        Assert.Equal("out/", result.Command!.OutputPath);
        Assert.False(result.Command!.NoOverwrite);
    }

    [Fact]
    public void Unpack_WithNoOverwrite_SetsNoOverwrite()
    {
        var result = FirmwareCliParser.Parse(["unpack", "boot.img", "-o", "out/", "--no-overwrite"]);

        Assert.True(result.Command!.NoOverwrite);
    }

    [Fact]
    public void Unpack_WithFormat_SetsFormatName()
    {
        var result = FirmwareCliParser.Parse(["unpack", "boot.img", "-o", "out/", "-f", "payload"]);

        Assert.Equal("payload", result.Command!.FormatName);
    }

    [Fact]
    public void Unpack_WithoutInput_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["unpack", "-o", "out/"]);

        Assert.True(result.IsError);
        Assert.Contains("requires a file path", result.Error!);
    }

    [Fact]
    public void Unpack_WithoutOutput_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["unpack", "boot.img"]);

        Assert.True(result.IsError);
        Assert.Contains("requires -o", result.Error!);
    }

    [Fact]
    public void Unpack_OFlagWithoutValue_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["unpack", "boot.img", "-o"]);

        Assert.True(result.IsError);
        Assert.Contains("'-o' requires a value", result.Error!);
    }

    [Fact]
    public void Unpack_UnknownFlag_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["unpack", "boot.img", "-o", "out/", "--bogus"]);

        Assert.True(result.IsError);
        Assert.Contains("Unknown flag", result.Error!);
    }

    [Fact]
    public void Pack_WithInputAndOutput_ParsesSuccessfully()
    {
        var result = FirmwareCliParser.Parse(["pack", "system_dir/", "-o", "system.img"]);

        Assert.False(result.IsError);
        Assert.Equal(FirmwareCliVerb.Pack, result.Command!.Verb);
        Assert.Equal("system_dir/", result.Command!.InputPath);
        Assert.Equal("system.img", result.Command!.OutputPath);
    }

    [Fact]
    public void Pack_WithFormat_SetsFormatName()
    {
        var result = FirmwareCliParser.Parse(["pack", "system_dir/", "-o", "system.img", "--format", "erofs"]);

        Assert.Equal("erofs", result.Command!.FormatName);
    }

    [Fact]
    public void Pack_WithoutInput_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["pack", "-o", "system.img"]);

        Assert.True(result.IsError);
        Assert.Contains("requires an input path", result.Error!);
    }

    [Fact]
    public void Pack_WithoutOutput_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["pack", "system_dir/"]);

        Assert.True(result.IsError);
        Assert.Contains("requires -o", result.Error!);
    }

    [Fact]
    public void UnknownSubcommand_ReturnsError()
    {
        var result = FirmwareCliParser.Parse(["bogus"]);

        Assert.True(result.IsError);
        Assert.Contains("Unknown firmware subcommand", result.Error!);
    }
}
