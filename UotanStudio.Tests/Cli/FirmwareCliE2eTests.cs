using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Cli;
using Xunit;

namespace UotanStudio.Tests.Cli;

/// <summary>
/// End-to-end tests for the extended firmware CLI subcommands: parsing/exit codes and real
/// file round-trips in a temp directory (checksum, info, disable-verification error paths).
/// Device-dependent and real-corpus scenarios are out of scope here.
/// <para>扩展固件 CLI 子命令的端到端测试：解析/退出码与临时目录中的真实文件回环
///（checksum、info、disable-verification 错误路径）。依赖设备与真实语料的场景不在本测试范围。</para>
/// </summary>
public sealed class FirmwareCliE2eTests : IDisposable
{
    private readonly string _root;

    public FirmwareCliE2eTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotanstudio-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Checksum_ExistingFile_PrintsSha256()
    {
        var input = Path.Combine(_root, "data.bin");
        var payload = Encoding.UTF8.GetBytes("uotanstudio checksum sample");
        await File.WriteAllBytesAsync(input, payload, TestContext.Current.CancellationToken);

        var (code, output) = await RunAsync(["firmware", "checksum", input]);
        Assert.Equal(0, code);

        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        Assert.Equal(expected, output.Trim());
    }

    [Fact]
    public async Task Checksum_MissingInput_ReturnsUsageError()
    {
        var (code, _) = await RunAsync(["firmware", "checksum", Path.Combine(_root, "missing.bin")]);
        Assert.Equal(3, code);
    }

    [Fact]
    public async Task Info_UnknownFormat_ReturnsFailure()
    {
        var input = Path.Combine(_root, "unknown.bin");
        await File.WriteAllBytesAsync(input, [0x01, 0x02, 0x03], TestContext.Current.CancellationToken);

        var (code, _) = await RunAsync(["firmware", "info", input]);
        Assert.Equal(3, code);
    }

    [Fact]
    public async Task DisableVerification_MissingInput_ReturnsFailure()
    {
        var (code, _) = await RunAsync([
            "firmware", "disable-verification",
            Path.Combine(_root, "missing-vbmeta.img"),
            "--output", Path.Combine(_root, "out-vbmeta.img"),
        ]);
        Assert.Equal(3, code);
    }

    [Fact]
    public async Task FirmwareCommand_HelpWithoutVerb_DoesNotThrow()
    {
        var (code, output) = await RunAsync(["firmware", "--help"]);
        Assert.Equal(0, code);
        Assert.Contains("checksum", output, StringComparison.Ordinal);
    }

    private static async Task<(int Code, string Output)> RunAsync(string[] args)
    {
        using var outputWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        var code = await CliProgram.RunAsync(args, outputWriter, errorWriter, TestContext.Current.CancellationToken);
        return (code, outputWriter.ToString() + errorWriter.ToString());
    }
}