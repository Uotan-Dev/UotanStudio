using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;
using UotanStudio.Firmware.SharedKernel.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed partial class CpioCompressedRoundTripTests
{
    public static bool NativeToolsEnabled =>
        Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") == "1" && OperatingSystem.IsWindows();

    public static bool ZstdAvailable => NativeToolsEnabled && ToolAvailable("zstd");
    public static bool Lz4Available => NativeToolsEnabled && ToolAvailable("lz4");
    public static bool LzopAvailable => NativeToolsEnabled && ToolAvailable("lzop");

    private static string ResolveTool(string tool)
    {
        var root = Environment.GetEnvironmentVariable("UOTAN_CPIO_TOOL_ROOT");
        return !string.IsNullOrWhiteSpace(root) && tool is "zstd" or "lz4" or "lzop"
            ? $"{root.TrimEnd('/')}/usr/bin/{tool}" : tool;
    }

    [Fact(Skip = "Requires UOTAN_NATIVE_TOOLS=1 and Windows WSL zstd/cpio.", SkipUnless = nameof(ZstdAvailable))]
    public Task PackAsync_IndependentZstd_PreservesContentAndEntryMetadata() =>
        VerifyIndependentCodecAsync("zst", "zstd", "Zstd", ["-q", "-c"]);

    [Theory(Skip = "Requires UOTAN_NATIVE_TOOLS=1 and Windows WSL lz4/cpio.", SkipUnless = nameof(Lz4Available))]
    [InlineData(false)]
    [InlineData(true)]
    public Task PackAsync_IndependentLz4_PreservesContentAndEntryMetadata(bool legacy) =>
        VerifyIndependentCodecAsync("lz4", "lz4", legacy ? "Lz4Legacy" : "Lz4",
            legacy ? ["-l", "-c"] : ["-c"]);

    [Theory(Skip = "Requires UOTAN_NATIVE_TOOLS=1 and Windows WSL lzop/cpio.", SkipUnless = nameof(LzopAvailable))]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task PackAsync_IndependentLzop_PreservesContentAndEntryMetadata(bool crc, bool compressible) =>
        VerifyIndependentCodecAsync("lzo", "lzop", "Lzop", crc ? ["--crc32", "-c"] : ["-c"], compressible);

    private async Task VerifyIndependentCodecAsync(string suffix, string tool, string compression, string[] arguments,
        bool compressible = false)
    {
        Directory.CreateDirectory(_root);
        var token = TestContext.Current.CancellationToken;
        var payload = new byte[256 * 1024 + 17];
        new Random(761).NextBytes(payload);
        if (compressible)
        {
            Array.Fill(payload, (byte)0x2A);
        }
        var source = Path.Combine(_root, $"input.cpio.{suffix}");
        var extracted = Path.Combine(_root, "extracted");
        var rebuilt = Path.Combine(_root, "rebuilt.bin");
        var encoded = await RunToolAsync(tool, arguments, BuildInteropArchive(payload), token);
        await File.WriteAllBytesAsync(source, encoded, token);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new CpioFormatHandler();

        Assert.True(handler.CanHandle(source));
        var unpacked = await handler.UnpackAsync(source, extracted, cancellationToken: token);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(extracted, "payload"), token));
        var metadata = RepackMetadata.Read(extracted);
        Assert.NotNull(metadata);
        Assert.Equal(compression, metadata.Configuration["cpio"]?["compression"]?.GetValue<string>());
        payload[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(Path.Combine(extracted, "payload"), payload, token);

        var packed = await handler.PackAsync(extracted, rebuilt, token);
        Assert.True(packed.Success, packed.Summary);
        var decoded = await RunToolAsync(tool, ["-d", "-c"], await File.ReadAllBytesAsync(rebuilt, token), token);
        Assert.Equal(payload, await RunToolAsync("cpio", ["-i", "--to-stdout", "payload"], decoded, token));
        using var stream = new MemoryStream(decoded);
        var entry = Assert.Single(new CpioArchive().ReadAll(stream));
        Assert.Equal(17UL, entry.Metadata.Inode);
        Assert.Equal(0x81A4U, entry.Metadata.Mode);
    }

    private static bool ToolAvailable(string tool)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--exec", "sh", "-c", "command -v \"$1\" >/dev/null && command -v cpio >/dev/null", "cpio-tools", ResolveTool(tool) },
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

    [Theory(Skip = "Set UOTAN_NATIVE_TOOLS=1 on Windows with WSL cpio/gzip/xz installed.", SkipUnless = nameof(NativeToolsEnabled))]
    [InlineData("gz", 5)]
    [InlineData("gz", 4096)]
    [InlineData("xz", 1024)]
    public async Task PackAsync_IndependentMultipartCompression_PreservesCompletePayload(string suffix, int split)
    {
        Directory.CreateDirectory(_root);
        var token = TestContext.Current.CancellationToken;
        var payload = new byte[24 * 1024 + 17];
        new Random(761).NextBytes(payload);
        var raw = BuildInteropArchive(payload);
        var encoded = await CompressMultipartAsync(raw, suffix, split, token);
        var tool = suffix == "gz" ? "gzip" : "xz";
        Assert.Equal(raw, await RunToolAsync(tool, ["-d", "-c"], encoded, token));
        var source = Path.Combine(_root, $"multipart.cpio.{suffix}");
        var extracted = Path.Combine(_root, "extracted");
        var rebuilt = Path.Combine(_root, "rebuilt.bin");
        await File.WriteAllBytesAsync(source, encoded, token);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new CpioFormatHandler();

        Assert.True(handler.CanHandle(source));
        var unpacked = await handler.UnpackAsync(source, extracted, cancellationToken: token);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(extracted, "payload"), token));
        payload[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(Path.Combine(extracted, "payload"), payload, token);
        var packed = await handler.PackAsync(extracted, rebuilt, token);
        Assert.True(packed.Success, packed.Summary);
        var decoded = await RunToolAsync(tool, ["-d", "-c"], await File.ReadAllBytesAsync(rebuilt, token), token);
        var extractedByCpio = await RunToolAsync("cpio", ["-i", "--to-stdout", "payload"], decoded, token);
        Assert.Equal(payload, extractedByCpio);
    }

    [Theory(Skip = "Set UOTAN_NATIVE_TOOLS=1 on Windows with WSL cpio/gzip/xz installed.", SkipUnless = nameof(NativeToolsEnabled))]
    [InlineData("gz", false)]
    [InlineData("gz", true)]
    [InlineData("xz", false)]
    [InlineData("xz", true)]
    public async Task UnpackAsync_DamagedMultipartCompression_PreservesExistingFile(string suffix, bool truncate)
    {
        Directory.CreateDirectory(_root);
        var token = TestContext.Current.CancellationToken;
        var raw = BuildInteropArchive(Encoding.ASCII.GetBytes(new string('x', 8192)));
        var encoded = await CompressMultipartAsync(raw, suffix, 1024, token);
        if (truncate)
        {
            encoded = encoded[..^8];
        }
        else
        {
            encoded[^(suffix == "gz" ? 8 : 12)] ^= 0x80;
        }
        var source = Path.Combine(_root, $"damaged.cpio.{suffix}");
        var extracted = Path.Combine(_root, "existing");
        Directory.CreateDirectory(extracted);
        var sentinel = Path.Combine(extracted, "payload");
        await File.WriteAllTextAsync(sentinel, "keep", token);
        await File.WriteAllBytesAsync(source, encoded, token);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);

        var result = await new CpioFormatHandler().UnpackAsync(source, extracted, cancellationToken: token);

        Assert.False(result.Success, result.Summary);
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel, token));
        Assert.Null(RepackMetadata.Read(extracted));
    }

    private static byte[] BuildInteropArchive(byte[] payload)
    {
        using var output = new MemoryStream();
        new CpioArchive().SaveAll(output,
        [
            new ArchiveEntry
            {
                Name = "payload",
                Data = payload,
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = 0x81A4, FileType = CpioFileType.Regular, LinkCount = 1, Inode = 17,
                },
            },
        ], ArchiveFormat.NewAscii);
        return output.ToArray();
    }

    private static async Task<byte[]> CompressMultipartAsync(byte[] raw, string suffix, int split,
        CancellationToken cancellationToken)
    {
        if (suffix == "xz")
        {
            return await RunToolAsync("xz", ["-c", "-T1", $"--block-size={split}", "--check=crc64"],
                raw, cancellationToken);
        }
        var first = await RunToolAsync("gzip", ["-n", "-c"], raw[..split], cancellationToken);
        var second = await RunToolAsync("gzip", ["-n", "-c"], raw[split..], cancellationToken);
        return [.. first, .. second];
    }
}