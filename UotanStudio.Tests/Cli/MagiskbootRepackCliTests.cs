using System.Buffers.Binary;
using UotanStudio.Cli;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using Xunit;

namespace UotanStudio.Tests.Cli;

/// <summary>
/// End-to-end coverage for <c>magiskboot repack</c>: a plain boot image round-trips through the CLI
/// unpack/repack, an AVB-footed source is refused, and a non-Boot directory is rejected.
/// <para>覆盖 <c>magiskboot repack</c>：未签名 boot 镜像在 CLI 解包/重打包回环中一致，含 AVB footer 的
/// 源被拒绝，非 Boot 目录被拒绝。</para>
/// </summary>
public sealed class MagiskbootRepackCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-cli-repack-" + Guid.NewGuid().ToString("N"));
    private string Image => Path.Combine(_root, "boot.img");
    private string Extracted => Path.Combine(_root, "extracted");
    private string Rebuilt => Path.Combine(_root, "rebuilt.img");

    public MagiskbootRepackCliTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Repack_UnsignedBootImage_RoundTripsThroughCli()
    {
        CreateBoot(version: 4);
        Assert.Equal(0, (await RunAsync(["magiskboot", "unpack", Image, "--output", Extracted])).Code);

        var (code, output) = await RunAsync(["magiskboot", "repack", Extracted, Rebuilt]);
        Assert.True(code == 0, $"repack failed (exit={code}):\n{output}");
        Assert.True(File.Exists(Rebuilt));

        using var parser = new BootImageParser();
        parser.ParseFile(Rebuilt);
        Assert.Equal(BootImageVersion.V4, parser.Header.HeaderVersion);
        Assert.Equal("console=ttyS0", parser.Header.CommandLine);
    }

    [Fact]
    public async Task Repack_AvbSignedBoot_FailsWithoutOverwritingExistingOutput()
    {
        CreateBoot(version: 4);
        AppendAvbFooter();
        Assert.Equal(0, (await RunAsync(["magiskboot", "unpack", Image, "--output", Extracted])).Code);
        File.WriteAllText(Rebuilt, "sentinel");

        var (code, output) = await RunAsync(["magiskboot", "repack", Extracted, Rebuilt]);
        Assert.Equal(3, code);
        Assert.Contains("AVB footer", output, StringComparison.Ordinal);
        Assert.Equal("sentinel", File.ReadAllText(Rebuilt));
    }

    [Fact]
    public async Task Repack_NonBootDirectory_Rejected()
    {
        Directory.CreateDirectory(Extracted);
        var (code, output) = await RunAsync(["magiskboot", "repack", Extracted, Rebuilt]);
        Assert.Equal(3, code);
        Assert.Contains("Boot repack tree", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Rebuilt));
    }

    private void CreateBoot(int version)
    {
        new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = (BootImageVersion)version,
                PageSize = version < 3 ? 2048u : 4096u,
                HeaderSize = version == 4 ? 1584u : version == 3 ? 1580u : 1664u,
                CommandLine = "console=ttyS0",
                ImageName = "test",
                KernelAddr = 0x8000,
                RamdiskAddr = 0x100000,
                TagsAddr = 0x100
            },
            Kernel = [1, 2, 3],
            Ramdisk = [4, 5, 6],
            Second = version < 3 ? [7, 8] : [],
            Signature = version == 4 ? [13, 14] : []
        }.PackToFile(Image);
    }

    private void AppendAvbFooter()
    {
        Span<byte> footer = stackalloc byte[AvbFooter.Size];
        "AVBf"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer[4..], AvbFooter.SupportedVersionMajor);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(12, 8), (ulong)new FileInfo(Image).Length);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(20, 8), (ulong)new FileInfo(Image).Length);
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(28, 8), (ulong)AvbFooter.Size);
        using var stream = new FileStream(Image, FileMode.Append, FileAccess.Write);
        stream.Write(footer);
    }

    private static async Task<(int Code, string Output)> RunAsync(string[] args)
    {
        using var outputWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        var code = await CliProgram.RunAsync(args, outputWriter, errorWriter, TestContext.Current.CancellationToken);
        return (code, outputWriter.ToString() + errorWriter.ToString());
    }
}