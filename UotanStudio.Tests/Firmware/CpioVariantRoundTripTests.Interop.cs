using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Archive.Writing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed partial class CpioVariantRoundTripTests
{
    public static bool NativeToolsEnabled =>
        Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") == "1" && OperatingSystem.IsWindows()
        && WslCpioAvailable();

    [Theory(Skip = "Requires UOTAN_NATIVE_TOOLS=1 and Windows WSL with GNU cpio installed.",
        SkipUnless = nameof(NativeToolsEnabled))]
    [InlineData(ArchiveFormat.NewAscii, "newc")]
    [InlineData(ArchiveFormat.CrcAscii, "crc")]
    [InlineData(ArchiveFormat.OldAscii, "odc")]
    [InlineData(ArchiveFormat.Binary, "bin")]
    public async Task SaveAll_GnuSupportedDialect_GnuCpioListsAndExtractsEntries(ArchiveFormat format, string dialect)
    {
        var token = TestContext.Current.CancellationToken;
        var payload = BuildBytes(4114, 31);
        var archive = new CpioArchive();
        using var stream = new MemoryStream();
        archive.SaveAll(stream, BuildMixedEntries(payload), format, new WriteOptions());

        var listing = await RunToolAsync("cpio", ["-t", "-H", dialect], stream.ToArray(), token);
        var names = Encoding.UTF8.GetString(listing)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["dir", "link", "payload"], names);

        var extracted = await RunToolAsync("cpio", ["-i", "-H", dialect, "--to-stdout", "payload"],
            stream.ToArray(), token);
        Assert.Equal(payload, extracted);
    }

    [Theory(Skip = "Requires UOTAN_NATIVE_TOOLS=1 and Windows WSL with GNU cpio installed.",
        SkipUnless = nameof(NativeToolsEnabled))]
    [InlineData("newc")]
    [InlineData("crc")]
    [InlineData("odc")]
    [InlineData("bin")]
    public async Task ReadEntries_GnuCpioProducedArchive_ParsesEveryEntry(string dialect)
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"cpio-variant-gnu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var payload = BuildBytes(300, 13);
            await File.WriteAllBytesAsync(Path.Combine(directory, "payload"), payload, token);
            await File.WriteAllTextAsync(Path.Combine(directory, "second"), "second text", token);
            var archivePath = Path.Combine(directory, "archive.cpio");
            await RunCpioCopyOutAsync(dialect, archivePath, directory, token);
            var raw = await File.ReadAllBytesAsync(archivePath, token);

            using var stream = new MemoryStream(raw, writable: false);
            var entries = new CpioArchive().ReadAll(stream);
            Assert.Equal(payload, entries.Single(entry => entry.Name == "payload").Data);
            Assert.Equal("second text", Encoding.UTF8.GetString(
                entries.Single(entry => entry.Name == "second").Data));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunCpioCopyOutAsync(string dialect, string archivePath, string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("wsl.exe")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("--exec");
        process.StartInfo.ArgumentList.Add("sh");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(
            "printf 'payload\\nsecond\\n' | cpio -o -H " + dialect + " --quiet -O '" + ToWslPath(archivePath) + "'");
        process.Start();
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, $"cpio -o -H {dialect}: {error}");
    }

    private static string ToWslPath(string path) =>
        $"/mnt/{char.ToLowerInvariant(path[0])}/{path[3..].Replace('\\', '/')}";

    private static bool WslCpioAvailable()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--exec", "sh", "-c", "command -v cpio >/dev/null" },
            },
        };
        try
        {
            process.Start();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<byte[]> RunToolAsync(string tool, string[] arguments, byte[] input,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cpio-variant-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "input");
        var outputPath = Path.Combine(directory, "output");
        await File.WriteAllBytesAsync(inputPath, input, cancellationToken);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("--exec");
        process.StartInfo.ArgumentList.Add("sh");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("input=$1; output=$2; shift 2; exec \"$@\" < \"$input\" > \"$output\"");
        process.StartInfo.ArgumentList.Add("cpio-variant-interop");
        process.StartInfo.ArgumentList.Add(ToWslPath(inputPath));
        process.StartInfo.ArgumentList.Add(ToWslPath(outputPath));
        process.StartInfo.ArgumentList.Add(tool);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cancellationToken));
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw;
            }
            Assert.True(process.ExitCode == 0, $"{tool}: {await errorTask}");
            return await File.ReadAllBytesAsync(outputPath, cancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}