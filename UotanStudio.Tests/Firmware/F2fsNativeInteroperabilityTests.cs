using System.Diagnostics;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class F2fsNativeInteroperabilityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Build_CompressedRawAndSparseDecodedImages_PassNativeDryRunCheckWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This interoperability check currently targets the configured Alpine WSL environment.");
        }

        var version = await RunWslAsync("fsck.f2fs", "-V");
        if (version.ExitCode != 0)
        {
            Assert.Skip("Alpine WSL with fsck.f2fs is unavailable.");
        }

        output.WriteLine(version.Output.Trim());
        Assert.Contains("fsck.f2fs 1.16.0", version.Output, StringComparison.Ordinal);

        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        var expected = CreateSourceFiles(source);
        var raw = Path.Combine(temp.Path, "system.raw.img");
        var sparse = Path.Combine(temp.Path, "system.sparse.img");
        var decoded = Path.Combine(temp.Path, "system.decoded.img");
        var rawResult = PackingService.BuildImage(source, raw, CreateSettings(sparse: false));
        var sparseResult = PackingService.BuildImage(source, sparse, CreateSettings(sparse: true));
        Assert.True(rawResult.Success, rawResult.Summary);
        Assert.True(sparseResult.Success, sparseResult.Summary);
        using (var sparseInput = File.OpenRead(sparse))
        {
            Span<byte> magic = stackalloc byte[4];
            sparseInput.ReadExactly(magic);
            Assert.Equal(0x3A, magic[0]);
            Assert.Equal(0xFF, magic[1]);
            Assert.Equal(0x26, magic[2]);
            Assert.Equal(0xED, magic[3]);
        }
        SparseImageConverter.ConvertSparseToRaw([sparse], decoded);

        AssertParserContent(raw, expected);
        AssertParserContent(decoded, expected);

        foreach (var image in new[] { raw, decoded })
        {
            var imageName = Path.GetFileName(image);
            var cleanCheck = await RunWslAsync("fsck.f2fs", "--dry-run", ToWslPath(image));
            output.WriteLine($"[{imageName}] clean-check exit={cleanCheck.ExitCode}\n{cleanCheck.Output.Trim()}");
            Assert.True(cleanCheck.ExitCode == 0, cleanCheck.Output);
            AssertCleanDryRunOutput(cleanCheck.Output);

            var fullCheck = await RunWslAsync("fsck.f2fs", "--dry-run", "-f", ToWslPath(image));
            output.WriteLine($"[{imageName}] full-check exit={fullCheck.ExitCode}\n{fullCheck.Output.Trim()}");
            Assert.True(fullCheck.ExitCode == 0, fullCheck.Output);
            Assert.DoesNotContain("Error:", fullCheck.Output, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertCleanDryRunOutput(string checkOutput)
    {
        Assert.DoesNotContain("Info: Force to fix corruption", checkOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Error:", checkOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[Fail", checkOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unreachable nat entries                        [Ok..]", checkOutput, StringComparison.Ordinal);
        Assert.Contains("SIT valid block bitmap checking                [Ok..]", checkOutput, StringComparison.Ordinal);
        Assert.Contains("valid_block_count matching with CP             [Ok..]", checkOutput, StringComparison.Ordinal);
        Assert.Contains("valid_node_count matching with CP (de lookup)  [Ok..]", checkOutput, StringComparison.Ordinal);
        Assert.Contains("valid_node_count matching with CP (nat lookup) [Ok..]", checkOutput, StringComparison.Ordinal);
        Assert.Contains("valid_inode_count matched with CP              [Ok..]", checkOutput, StringComparison.Ordinal);
        Assert.Contains("free segment_count matched with CP             [Ok..]", checkOutput, StringComparison.Ordinal);
        Assert.Contains("next block offset is free                      [Ok..]", checkOutput, StringComparison.Ordinal);
        Assert.Contains("other corrupted bugs                           [Ok..]", checkOutput, StringComparison.Ordinal);
    }

    private static ProjectSettingViewModel CreateSettings(bool sparse)
    {
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "F2FS",
            ImageFormat = sparse ? "SPARSE" : "RAW",
            SparseOutput = sparse,
            F2fsCompressionEnabled = true,
            F2fsCompressionAlgorithm = nameof(CompressionAlgorithm.Zstd),
            F2fsCompressionLevel = 3,
        });
        return settings;
    }

    private static Dictionary<string, byte[]> CreateSourceFiles(string source)
    {
        var random = new byte[256 * 1024];
        new Random(0x46324653).NextBytes(random);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["compressible.bin"] = Enumerable.Repeat((byte)'A', 256 * 1024).ToArray(),
            ["random.bin"] = random,
            ["short-tail.bin"] = Enumerable.Repeat((byte)'T', 70 * 1024 + 37).ToArray(),
        };
        foreach (var (name, content) in files)
        {
            File.WriteAllBytes(Path.Combine(source, name), content);
        }

        return files;
    }

    private static void AssertParserContent(string image, IReadOnlyDictionary<string, byte[]> expected)
    {
        using var parser = new F2fsParser(image);
        var entries = parser.ListFiles().ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        Assert.NotEqual(0u, entries["compressible.bin"].Flags & F2fsInode.ComprFlag);
        foreach (var (path, content) in expected)
        {
            using var actual = new MemoryStream();
            parser.ReadFile(path, actual);
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(actual.ToArray()));
        }
    }

    private static async Task<(int ExitCode, string Output)> RunWslAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add("Alpine");
        start.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start WSL.");
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static string ToWslPath(string path)
    {
        var full = Path.GetFullPath(path);
        return $"/mnt/{char.ToLowerInvariant(full[0])}/{full[3..].Replace('\\', '/')}";
    }
}