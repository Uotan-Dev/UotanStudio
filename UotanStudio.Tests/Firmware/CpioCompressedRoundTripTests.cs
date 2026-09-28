using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Archive.Writing;
using UotanStudio.Firmware.Core.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed partial class CpioCompressedRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cpio-compressed-{Guid.NewGuid():N}");

    [Theory(Skip = "Requires UOTAN_NATIVE_TOOLS=1 on Windows with WSL cpio/gzip/xz/bzip2/lzma.", SkipUnless = nameof(NativeToolsEnabled))]
    [InlineData("gz", "gzip")]
    [InlineData("xz", "xz")]
    [InlineData("bz2", "bzip2")]
    [InlineData("lzma", "lzma")]
    public async Task PackAsync_IndependentCompressedCrcArchive_PreservesContainerAndEntryHeaders(
        string suffix, string utility)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, $"ramdisk.cpio.{suffix}");
        var extracted = Path.Combine(_root, "extracted");
        var rebuilt = Path.Combine(_root, "rebuilt.bin");
        var token = TestContext.Current.CancellationToken;
        var archive = new CpioArchive();
        using var plain = new MemoryStream();
        archive.SaveAll(plain,
        [
            new ArchiveEntry
            {
                Name = "etc",
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = 0x41ED, FileType = CpioFileType.Directory,
                    Inode = 7, UserId = 100, GroupId = 200, LinkCount = 2,
                    ModificationTimeUnixSeconds = 1_700_000_100,
                },
            },
            new ArchiveEntry
            {
                Name = "etc/build.prop",
                Data = Encoding.UTF8.GetBytes("before\n"),
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = 0x81A4, FileType = CpioFileType.Regular,
                    Inode = 8, UserId = 100, GroupId = 200, LinkCount = 1,
                    ModificationTimeUnixSeconds = 1_700_000_200,
                    DeviceMajor = 3, DeviceMinor = 4,
                },
            },
        ], ArchiveFormat.CrcAscii, new WriteOptions());
        var independentlyCompressed = await RunToolAsync(utility,
            utility switch
            {
                "gzip" => ["-n", "-c"],
                "bzip2" => ["-1", "-c"],
                "lzma" => ["-0", "-c"],
                _ => ["-c"],
            }, plain.ToArray(), token);
        await File.WriteAllBytesAsync(source, independentlyCompressed, token);

        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new CpioFormatHandler();
        Assert.True(handler.CanHandle(source));
        var unpacked = await handler.UnpackAsync(source, extracted, cancellationToken: token);
        Assert.True(unpacked.Success, unpacked.Summary);
        var saved = RepackMetadata.Read(extracted);
        Assert.NotNull(saved);
        Assert.Equal("CrcAscii", saved.Configuration["cpio"]?["format"]?.GetValue<string>());
        Assert.Equal(utility switch
        {
            "gzip" => "Gzip",
            "xz" => "Xz",
            "bzip2" => "Bzip2",
            _ => "Lzma",
        },
            saved.Configuration["cpio"]?["compression"]?.GetValue<string>());

        await File.WriteAllTextAsync(Path.Combine(extracted, "etc", "build.prop"), "after\n", token);
        var packed = await handler.PackAsync(extracted, rebuilt, token);
        Assert.True(packed.Success, packed.Summary);

        var result = await RunToolAsync(utility, ["-d", "-c"],
            await File.ReadAllBytesAsync(rebuilt, token), token);
        var listing = await RunToolAsync("cpio", ["-it", "-H", "crc"], result, token);
        Assert.Contains("etc/build.prop", Encoding.UTF8.GetString(listing));
        using var stream = new MemoryStream(result);
        Assert.Equal(ArchiveFormat.CrcAscii, archive.DetectFormat(stream));
        var entries = archive.ReadAll(stream);
        var file = Assert.Single(entries, entry => entry.Name == "etc/build.prop");
        Assert.Equal("after\n", Encoding.UTF8.GetString(file.Data));
        Assert.Equal(8UL, file.Metadata.Inode);
        Assert.Equal(100U, file.Metadata.UserId);
        Assert.Equal(200U, file.Metadata.GroupId);
        Assert.Equal(3U, file.Metadata.DeviceMajor);
        Assert.Equal(4U, file.Metadata.DeviceMinor);
        Assert.Equal(1_700_000_200, file.Metadata.ModificationTimeUnixSeconds);
        if (suffix == "gz")
        {
            Assert.Equal(independentlyCompressed[4..10],
                (await File.ReadAllBytesAsync(rebuilt, token))[4..10]);
        }
        if (suffix == "xz")
        {
            Assert.Equal(independentlyCompressed[7], saved.Configuration["cpio"]?["checkType"]?.GetValue<byte>());
            Assert.Equal(independentlyCompressed[7], (await File.ReadAllBytesAsync(rebuilt, token))[7]);
            Assert.InRange(saved.Configuration["cpio"]?["dictionarySize"]?.GetValue<uint>() ?? 0,
                4096U, 128U * 1024 * 1024);
        }
        if (suffix == "bz2")
        {
            Assert.Equal(independentlyCompressed[3], (await File.ReadAllBytesAsync(rebuilt, token))[3]);
        }
        if (suffix == "lzma")
        {
            Assert.Equal(independentlyCompressed[..5],
                (await File.ReadAllBytesAsync(rebuilt, token))[..5]);
        }
    }

    [Fact]
    public async Task PackAsync_CancelledOrInvalidMetadata_PreservesExistingOutput()
    {
        Directory.CreateDirectory(_root);
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        await File.WriteAllTextAsync(Path.Combine(extracted, "init.rc"), "boot",
            TestContext.Current.CancellationToken);
        var target = Path.Combine(_root, "ramdisk.cpio.gz");
        var original = Encoding.UTF8.GetBytes("old output");
        await File.WriteAllBytesAsync(target, original, TestContext.Current.CancellationToken);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new CpioFormatHandler().PackAsync(extracted, target, cancelled.Token));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));

        var metadata = new RepackMetadata
        {
            FormatName = "CPIO/TAR Archive",
            SourceFileName = "ramdisk.cpio.gz",
        };
        metadata.Configuration["cpio"] = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = "CrcAscii",
            ["compression"] = "Unsupported",
            ["entries"] = new System.Text.Json.Nodes.JsonArray(),
        };
        await metadata.SaveAsync(extracted, TestContext.Current.CancellationToken);
        var result = await new CpioFormatHandler().PackAsync(extracted, target,
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact(Skip = "Requires UOTAN_NATIVE_TOOLS=1 on Windows with WSL gzip.", SkipUnless = nameof(NativeToolsEnabled))]
    public async Task PackAsync_GzipOptionalHeaderAndDotPrefixedNames_PreservesBoth()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "root.cpio.gz");
        var extracted = Path.Combine(_root, "out");
        var target = Path.Combine(_root, "again.cpio.gz");
        var token = TestContext.Current.CancellationToken;
        var archive = new CpioArchive();
        using var plain = new MemoryStream();
        archive.SaveAll(plain,
        [
            new ArchiveEntry
            {
                Name = "./etc",
                Data = [],
                Metadata = new ArchiveEntryMetadata { Mode = 0x41ED, FileType = CpioFileType.Directory, LinkCount = 1 },
            },
            new ArchiveEntry
            {
                Name = "./etc/config",
                Data = Encoding.UTF8.GetBytes("old"),
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = 0x81A4, FileType = CpioFileType.Regular, UserId = 42, GroupId = 43,
                    LinkCount = 1, Inode = 19,
                },
            },
        ], ArchiveFormat.NewAscii);
        var compressed = await RunToolAsync("gzip", ["-n", "-c"], plain.ToArray(), token);
        var filename = Encoding.ASCII.GetBytes("source.cpio\0");
        var optionalHeader = new byte[10 + filename.Length];
        compressed.AsSpan(0, 10).CopyTo(optionalHeader);
        optionalHeader[3] = 8;
        filename.CopyTo(optionalHeader.AsSpan(10));
        var named = optionalHeader.Concat(compressed.AsSpan(10).ToArray()).ToArray();
        await File.WriteAllBytesAsync(source, named, token);

        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new CpioFormatHandler();
        Assert.True((await handler.UnpackAsync(source, extracted, cancellationToken: token)).Success);
        await File.WriteAllTextAsync(Path.Combine(extracted, "etc", "config"), "new", token);
        var repack = await handler.PackAsync(extracted, target, token);
        Assert.True(repack.Success, repack.Summary);
        var rebuilt = await File.ReadAllBytesAsync(target, token);
        Assert.Equal(optionalHeader, rebuilt[..optionalHeader.Length]);
        var raw = await RunToolAsync("gzip", ["-d", "-c"], rebuilt, token);
        using var input = new MemoryStream(raw);
        var entry = Assert.Single(archive.ReadAll(input), entry => entry.Name == "./etc/config");
        Assert.Equal("new", Encoding.UTF8.GetString(entry.Data));
        Assert.Equal(42U, entry.Metadata.UserId);
        Assert.Equal(43U, entry.Metadata.GroupId);
        Assert.Equal(19UL, entry.Metadata.Inode);
    }

    [Fact(Skip = "Requires UOTAN_NATIVE_TOOLS=1 on Windows with WSL gzip.", SkipUnless = nameof(NativeToolsEnabled))]
    public async Task CanHandle_CompressedNonCpio_DoesNotClaimArchive()
    {
        Directory.CreateDirectory(_root);
        var misleading = Path.Combine(_root, "not-archive.cpio.gz");
        var compressed = await RunToolAsync("gzip", ["-n", "-c"],
            Encoding.UTF8.GetBytes("ordinary content"), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(misleading, compressed, TestContext.Current.CancellationToken);
        Assert.False(new CpioFormatHandler().CanHandle(misleading));
    }

    [Fact(Skip = "Requires UOTAN_NATIVE_TOOLS=1 on Windows with WSL gzip.", SkipUnless = nameof(NativeToolsEnabled))]
    public async Task PackAsync_RootAndSymbolicLink_RestoreArchiveTypes()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "links.cpio.gz");
        var extracted = Path.Combine(_root, "out");
        var target = Path.Combine(_root, "again.cpio.gz");
        var token = TestContext.Current.CancellationToken;
        var archive = new CpioArchive();
        using var plain = new MemoryStream();
        archive.SaveAll(plain,
        [
            new ArchiveEntry
            {
                Name = ".",
                Data = [],
                Metadata = new ArchiveEntryMetadata { Mode = 0x41ED, FileType = CpioFileType.Directory, Inode = 1, LinkCount = 2 },
            },
            new ArchiveEntry
            {
                Name = "config",
                Data = Encoding.UTF8.GetBytes("data"),
                Metadata = new ArchiveEntryMetadata { Mode = 0x81A4, FileType = CpioFileType.Regular, Inode = 2, LinkCount = 1 },
            },
            new ArchiveEntry
            {
                Name = "link",
                LinkName = "config",
                Data = [],
                Metadata = new ArchiveEntryMetadata { Mode = 0xA1FF, FileType = CpioFileType.SymbolicLink, Inode = 3, LinkCount = 1 },
            },
        ], ArchiveFormat.NewAscii);
        await File.WriteAllBytesAsync(source,
            await RunToolAsync("gzip", ["-n", "-c"], plain.ToArray(), token), token);

        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new CpioFormatHandler();
        var unpacked = await handler.UnpackAsync(source, extracted, cancellationToken: token);
        Assert.True(unpacked.Success, unpacked.Summary);
        var repacked = await handler.PackAsync(extracted, target, token);
        Assert.True(repacked.Success, repacked.Summary);
        var raw = await RunToolAsync("gzip", ["-d", "-c"],
            await File.ReadAllBytesAsync(target, token), token);
        using var input = new MemoryStream(raw);
        var entries = archive.ReadAll(input);
        Assert.Equal(1UL, Assert.Single(entries, entry => entry.Name == ".").Metadata.Inode);
        var link = Assert.Single(entries, entry => entry.Name == "link");
        Assert.Equal(CpioFileType.SymbolicLink, link.Metadata.FileType);
        Assert.Equal("config", link.LinkName);
    }

    [Fact(Skip = "Requires UOTAN_NATIVE_TOOLS=1 on Windows with WSL gzip.", SkipUnless = nameof(NativeToolsEnabled))]
    public async Task PackAsync_DivergentHardlinks_PreservesExistingOutput()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "hardlinks.cpio.gz");
        var extracted = Path.Combine(_root, "out");
        var target = Path.Combine(_root, "again.cpio.gz");
        var token = TestContext.Current.CancellationToken;
        var archive = new CpioArchive();
        using var plain = new MemoryStream();
        archive.SaveAll(plain,
        [
            new ArchiveEntry
            {
                Name = "first",
                Data = Encoding.ASCII.GetBytes("linked"),
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = 0x81A4, FileType = CpioFileType.Regular,
                    Inode = 13, LinkCount = 2, DeviceMajor = 1,
                },
            },
            new ArchiveEntry
            {
                Name = "second",
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = 0x81A4, FileType = CpioFileType.Regular,
                    Inode = 13, LinkCount = 2, DeviceMajor = 1,
                },
            },
        ], ArchiveFormat.NewAscii);
        await File.WriteAllBytesAsync(source,
            await RunToolAsync("gzip", ["-n", "-c"], plain.ToArray(), token), token);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new CpioFormatHandler();
        Assert.True((await handler.UnpackAsync(source, extracted, cancellationToken: token)).Success);
        var packed = await handler.PackAsync(extracted, target, token);
        Assert.True(packed.Success, packed.Summary);
        var initial = await File.ReadAllBytesAsync(target, token);
        var raw = await RunToolAsync("gzip", ["-d", "-c"], initial, token);
        using (var input = new MemoryStream(raw))
        {
            var members = archive.ReadAll(input);
            Assert.Equal(13UL, Assert.Single(members, entry => entry.Name == "first").Metadata.Inode);
            Assert.Equal(13UL, Assert.Single(members, entry => entry.Name == "second").Metadata.Inode);
        }
        await File.WriteAllTextAsync(Path.Combine(extracted, "second"), "different", token);
        var rejected = await handler.PackAsync(extracted, target, token);
        Assert.False(rejected.Success);
        Assert.Equal(initial, await File.ReadAllBytesAsync(target, token));
    }

    [Fact(Skip = "Requires UOTAN_NATIVE_TOOLS=1 on Windows with WSL gzip.", SkipUnless = nameof(NativeToolsEnabled))]
    public async Task UnpackAsync_DuplicateProjectedPaths_FailsBeforeExtracting()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "duplicate.cpio.gz");
        var destination = Path.Combine(_root, "existing");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "config");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(sentinel, "keep", token);
        using var plain = new MemoryStream();
        new CpioArchive().SaveAll(plain,
        [
            new ArchiveEntry
            {
                Name = "./config",
                Data = Encoding.ASCII.GetBytes("first"),
                Metadata = new ArchiveEntryMetadata { Mode = 0x81A4, FileType = CpioFileType.Regular, LinkCount = 1 },
            },
            new ArchiveEntry
            {
                Name = "config",
                Data = Encoding.ASCII.GetBytes("second"),
                Metadata = new ArchiveEntryMetadata { Mode = 0x81A4, FileType = CpioFileType.Regular, LinkCount = 1 },
            },
        ], ArchiveFormat.NewAscii);
        await File.WriteAllBytesAsync(source,
            await RunToolAsync("gzip", ["-n", "-c"], plain.ToArray(), token), token);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var result = await new CpioFormatHandler().UnpackAsync(source, destination, cancellationToken: token);
        Assert.False(result.Success);
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel, token));
        Assert.Null(RepackMetadata.Read(destination));
    }

    private static async Task<byte[]> RunToolAsync(string tool, string[] arguments, byte[] input,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cpio-tool-{Guid.NewGuid():N}");
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
        process.StartInfo.ArgumentList.Add("cpio-interop");
        process.StartInfo.ArgumentList.Add(ToWslPath(inputPath));
        process.StartInfo.ArgumentList.Add(ToWslPath(outputPath));
        if (Environment.GetEnvironmentVariable("UOTAN_CPIO_TOOL_ROOT") is { Length: > 0 } root)
        {
            process.StartInfo.ArgumentList.Add("env");
            process.StartInfo.ArgumentList.Add($"LD_LIBRARY_PATH={root.TrimEnd('/')}/usr/lib/x86_64-linux-gnu");
        }
        process.StartInfo.ArgumentList.Add(ResolveTool(tool));
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

        static string ToWslPath(string path) =>
            $"/mnt/{char.ToLowerInvariant(path[0])}/{path[3..].Replace('\\', '/')}";
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}