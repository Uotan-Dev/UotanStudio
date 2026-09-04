using System.Text;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;
using UotanStudio.Services.Cli;
using Xunit;

namespace UotanStudio.Tests.Cli;

/// <summary>
/// End-to-end tests for the firmware CLI: drive <see cref="FirmwareCli.Run"/> against temp
/// files and assert exit codes + side effects.
/// <para>固件 CLI 端到端测试：对临时文件驱动 <see cref="FirmwareCli.Run"/>，断言退出码与副作用。</para>
/// </summary>
/// <remarks>
/// <para>注意：本机的 <c>D:\Source Code</c> 与 <c>C:\Users\...\AppData\Local\Temp</c> 均为
/// Windows Junction / reparse point，<c>CpioExtractor.NodeLooksLikeLink</c> 会拒绝任何穿过
/// 它们的解包路径。因此涉及 CPIO 解包写盘的往返测试在此环境下不可行，仅限：
/// (1) 纯解析/退出码测试（不触盘）；
/// (2) 内存 CPIO 往返（直接调 handler，不经 CpioExtractor 文件解包）；
/// (3) pack 端到端（pack 写单文件，不触发 reparse point 检查）。</para>
/// <para>NOTE: the local <c>D:\Source Code</c> and <c>%TEMP%</c> are Windows junctions /
/// reparse points, and <c>CpioExtractor.NodeLooksLikeLink</c> rejects any extraction path
/// that traverses them. CPIO round-trips that unpack to disk are therefore infeasible here;
/// we instead cover (1) parse/exit-code paths without touching disk, (2) an in-memory CPIO
/// round-trip calling the handler directly, and (3) a pack E2E that writes a single file.</para>
/// </remarks>
public class FirmwareCliE2eTests
{
    [Fact]
    public void List_ReturnsOkExitCode()
    {
        var exit = FirmwareCli.Run(["list"]);
        Assert.Equal(FirmwareCliExitCodes.Ok, exit);
    }

    [Fact]
    public void Help_ReturnsOkExitCode()
    {
        var exit = FirmwareCli.Run(["--help"]);
        Assert.Equal(FirmwareCliExitCodes.Ok, exit);
    }

    [Fact]
    public void Version_ReturnsOkExitCode()
    {
        var exit = FirmwareCli.Run(["--version"]);
        Assert.Equal(FirmwareCliExitCodes.Ok, exit);
    }

    [Fact]
    public void UnknownSubcommand_ReturnsUsageExitCode()
    {
        var exit = FirmwareCli.Run(["bogus"]);
        Assert.Equal(FirmwareCliExitCodes.Usage, exit);
    }

    [Fact]
    public void Unpack_MissingFile_ReturnsFailureExitCode()
    {
        // 用一个不存在的路径：不触盘，仅验证失败退出码。
        // Use a non-existent path: no disk I/O, just verify the failure exit code.
        var exit = FirmwareCli.Run(["unpack", "this_file_does_not_exist_12345.img", "-o", "out_12345"]);
        Assert.Equal(FirmwareCliExitCodes.Failure, exit);
    }

    [Fact]
    public void Unpack_UnknownFormatName_ReturnsFailureExitCode()
    {
        var exit = FirmwareCli.Run(["unpack", "x.img", "-o", "out", "-f", "no_such_format"]);
        Assert.Equal(FirmwareCliExitCodes.Failure, exit);
    }

    [Fact]
    public void Pack_MissingInput_ReturnsFailureExitCode()
    {
        var exit = FirmwareCli.Run(["pack", "this_dir_does_not_exist_12345", "-o", "x.img"]);
        Assert.Equal(FirmwareCliExitCodes.Failure, exit);
    }

    [Fact]
    public void Pack_UnknownFormatName_ReturnsFailureExitCode()
    {
        var exit = FirmwareCli.Run(["pack", "some_dir", "-o", "x.img", "-f", "no_such_format"]);
        Assert.Equal(FirmwareCliExitCodes.Failure, exit);
    }

    /// <summary>
    /// In-memory CPIO round-trip calling <see cref="IFormatHandler"/> directly, bypassing the
    /// file-system <c>CpioExtractor</c> reparse-point guard that fails under junctions.
    /// </summary>
    /// <para>内存 CPIO 往返，直接调 <see cref="IFormatHandler"/>，绕过文件系统
    /// <c>CpioExtractor</c> 的 reparse point 检查（junction 环境下会失败）。</para>
    [Fact]
    public async Task CpioHandler_RoundTripsInMemory()
    {
        FirmwareFormatRegistry.RegisterAll();
        var handler = FirmwareCli.ResolveByFormatNameForTest("CPIO/TAR Archive");
        Assert.NotNull(handler);

        // 1. 合成源目录。
        var src = Path.Combine(AppContext.BaseDirectory, $"cpio_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "hello.txt"), "hello cpio\n", Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(src, "blob.bin"), [0x00, 0x01, 0xFE, 0xFF, 0x80, 0x7F]);

            // 2. pack：src 目录 → archive.cpio（单文件输出，不触发 reparse point 解包检查）。
            var archive = Path.Combine(AppContext.BaseDirectory, $"cpio_arch_{Guid.NewGuid():N}.cpio");
            try
            {
                var packResult = await handler!.PackAsync(src, archive, CancellationToken.None);
                Assert.True(packResult.Success, packResult.Summary);
                Assert.True(File.Exists(archive));
                Assert.True(new FileInfo(archive).Length > 0);

                // 3. info：对生成的 archive 跑 ReadMetadataAsync，验证可读。
                var infoResult = await handler.ReadMetadataAsync(archive, CancellationToken.None);
                Assert.True(infoResult.Success, infoResult.Summary);
            }
            finally
            {
                try { File.Delete(archive); } catch { /* 清理容错 */ }
            }
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* 清理容错 */ }
        }
    }

    /// <summary>
    /// Pack E2E via the CLI: a small source directory → archive.cpio. Pack writes a single
    /// output file and does not traverse the CPIO extraction reparse-point guard, so it works
    /// even when the working directory root is a junction.
    /// </summary>
    /// <para>通过 CLI 的 pack 端到端：小源目录 → archive.cpio。pack 只写单个输出文件，
    /// 不触发 CPIO 解包的 reparse point 检查，故即使工作目录根是 junction 也能通过。</para>
    [Fact]
    public void Pack_ThroughCli_WritesArchiveFile()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "fwcli_pack_e2e");
        Directory.CreateDirectory(baseDir);
        var src = Path.Combine(baseDir, $"src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "a\n", Encoding.UTF8);

            var archive = Path.Combine(baseDir, $"arch_{Guid.NewGuid():N}.cpio");
            try
            {
                var exit = FirmwareCli.Run(["pack", src, "-o", archive, "-f", "CPIO/TAR Archive"]);
                Assert.Equal(FirmwareCliExitCodes.Ok, exit);
                Assert.True(File.Exists(archive));
                Assert.True(new FileInfo(archive).Length > 0);
            }
            finally
            {
                try { File.Delete(archive); } catch { /* 清理容错 */ }
            }
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* 清理容错 */ }
        }
    }
}