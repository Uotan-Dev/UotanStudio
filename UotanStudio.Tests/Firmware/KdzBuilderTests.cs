using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Build;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Extraction;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Models;
using UotanStudio.Firmware.SharedKernel.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class KdzBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uot-kdz-build-" + Guid.NewGuid().ToString("N"));

    public KdzBuilderTests() => Directory.CreateDirectory(_root);

    public static bool ToolsEnabled => Environment.GetEnvironmentVariable("UOTAN_KDZTOOLS") is { Length: > 0 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_PhysicalOrderDiffers_PreservesDirectoryAndContents(bool reverse)
    {
        var entries = Inputs();
        var output = Path.Combine(_root, "new.kdz");

        await KdzBuilder.BuildAsync(entries, output, payloadOrder: reverse ? [1, 0] : null,
            cancellationToken: TestContext.Current.CancellationToken);

        var result = KdzExtractor.Parse(output);
        Assert.Equal(2, result.HeaderType);
        Assert.Equal(entries.Select(entry => entry.Name), result.Partitions.Select(part => part.Name));
        Assert.Equal(4096UL, result.Partitions[reverse ? 1 : 0].Offset);
        var extracted = Path.Combine(_root, "out");
        Assert.Equal(2, KdzExtractor.Extract(output, extracted, cancellationToken: TestContext.Current.CancellationToken));
        foreach (var entry in entries)
        {
            Assert.Equal(File.ReadAllBytes(entry.FilePath), File.ReadAllBytes(Path.Combine(extracted, entry.Name)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Handler_Repack_LegacyKdzPreservesHeaderTypeAndContents(int headerType)
    {
        var entries = Inputs();
        var source = Path.Combine(_root, $"legacy-{headerType}.kdz");
        await KdzBuilder.BuildAsync(entries, source, cancellationToken: TestContext.Current.CancellationToken);
        var bytes = File.ReadAllBytes(source);
        var magic = headerType == 0 ? DzConstants.KdzHeaderV0 : DzConstants.KdzHeaderV1;
        magic.CopyTo(bytes, 0);
        File.WriteAllBytes(source, bytes);

        var extracted = Path.Combine(_root, $"legacy-{headerType}-extracted");
        var rebuilt = Path.Combine(_root, $"legacy-{headerType}-rebuilt.kdz");
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new DzFormatHandler();

        var unpacked = await handler.UnpackAsync(source, extracted,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.Equal(headerType, RepackMetadata.Read(extracted)!.Configuration["kdz"]!["version"]!.GetValue<int>());

        var packed = await handler.PackAsync(extracted, rebuilt, TestContext.Current.CancellationToken);

        Assert.True(packed.Success, packed.Summary);
        Assert.Equal(headerType, KdzExtractor.Parse(rebuilt).HeaderType);
        Assert.Equal(magic, File.ReadAllBytes(rebuilt)[..8]);
        foreach (var entry in entries)
        {
            Assert.Equal(File.ReadAllBytes(entry.FilePath),
                File.ReadAllBytes(Path.Combine(extracted, entry.Name)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackAsync_RelocatedMetadataWithoutSource_PreservesUnmodifiedBytesAndModifiedMembers(bool zeroTerminator)
    {
        var token = TestContext.Current.CancellationToken;
        var source = await SourceWithGapsAsync();
        if (zeroTerminator)
        {
            var prefixBytes = await File.ReadAllBytesAsync(source, token);
            prefixBytes.AsSpan(281, 272).CopyTo(prefixBytes.AsSpan(280));
            prefixBytes[552] = 0;
            await File.WriteAllBytesAsync(source, prefixBytes, token);
        }
        var original = await File.ReadAllBytesAsync(source, token);
        var extracted = Path.Combine(_root, "extracted");
        var moved = Path.Combine(_root, "relocated");
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new DzFormatHandler();
        var unpacked = await handler.UnpackAsync(source, extracted, cancellationToken: token);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.True(RepackMetadata.Read(extracted)!.CanPackDirectory);
        File.Delete(source);
        Directory.Move(extracted, moved);
        var rebuilt = Path.Combine(_root, "rebuilt.kdz");

        var unchanged = await handler.PackAsync(moved, rebuilt, token);

        Assert.True(unchanged.Success, unchanged.Summary);
        Assert.Equal(original, await File.ReadAllBytesAsync(rebuilt, token));
        var edited = new byte[65539];
        new Random(519).NextBytes(edited);
        await File.WriteAllBytesAsync(Path.Combine(moved, "firmware.dz"), edited, token);
        var changed = await handler.PackAsync(moved, rebuilt, token);
        Assert.True(changed.Success, changed.Summary);
        var parts = KdzExtractor.Parse(rebuilt).Partitions;
        Assert.Equal((ulong)edited.Length, parts[0].Length);
        Assert.Equal(4108UL, parts[0].Offset);
        Assert.Equal(4096UL, parts[1].Offset);
        var bytes = await File.ReadAllBytesAsync(rebuilt, token);
        Assert.Equal(original[1000..1100], bytes[1000..1100]);
        Assert.Equal(original[4101..4108], bytes[4101..4108]);
        Assert.Equal(original[^11..], bytes[^11..]);
        KdzExtractor.Extract(rebuilt, Path.Combine(_root, "edited"),
            cancellationToken: token);
        Assert.Equal(edited, await File.ReadAllBytesAsync(Path.Combine(_root, "edited", "firmware.dz"), token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65537)]
    public async Task BuildAsync_SingleMember_HandlesEmptyAndUnalignedContent(int length)
    {
        var path = Path.Combine(_root, "member.bin");
        var bytes = new byte[length];
        new Random(73).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        var output = Path.Combine(_root, "single.kdz");

        await KdzBuilder.BuildAsync([new("member.bin", path)], output,
            cancellationToken: TestContext.Current.CancellationToken);

        var part = Assert.Single(KdzExtractor.Parse(output).Partitions);
        Assert.Equal((ulong)length, part.Length);
        Assert.Equal(4096UL, part.Offset);
        Assert.Equal(bytes, File.ReadAllBytes(output)[4096..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(552)]
    [InlineData(67108865)]
    public async Task BuildAsync_InvalidDataStart_RejectsWithoutPublishing(long dataStart)
    {
        var output = Path.Combine(_root, "invalid.kdz");

        await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(Inputs(), output, dataStart,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Copy_CancellationBetweenReads_StopsBeforeNextRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new CancelingStream(cancellation);
        using var output = new MemoryStream();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            KdzBuilder.Copy(source, output, source.Length, new byte[64 * 1024], cancellation.Token));

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(64 * 1024, source.LargestRequest);
        Assert.Equal(64 * 1024, output.Length);
    }

    [Theory]
    [InlineData("missing-member")]
    [InlineData("bad-artifact")]
    [InlineData("bad-record")]
    [InlineData("bad-order")]
    [InlineData("bad-gap")]
    [InlineData("escape")]
    public async Task PackAsync_InvalidPreservation_PreservesExistingOutput(string corruption)
    {
        var token = TestContext.Current.CancellationToken;
        var source = await SourceWithGapsAsync();
        var extracted = Path.Combine(_root, "extracted");
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var handler = new DzFormatHandler();
        Assert.True((await handler.UnpackAsync(source, extracted, cancellationToken: token)).Success);
        var metadata = RepackMetadata.Read(extracted)!;
        var config = metadata.Configuration["kdz"]!;
        var metadataDirectory = Path.GetDirectoryName(RepackMetadata.ResolveActualMetadataPath(extracted))!;
        switch (corruption)
        {
            case "missing-member":
                File.Delete(Path.Combine(extracted, "firmware.dz"));
                break;
            case "bad-artifact":
                File.WriteAllBytes(Path.Combine(metadataDirectory, config["layoutFile"]!.GetValue<string>()), [1, 2, 3]);
                break;
            case "bad-record":
                config["entries"]![0]!["recordOffset"] = 1000;
                break;
            case "bad-order":
                config["order"]![1] = 1;
                break;
            case "bad-gap":
                config["gaps"]![0] = -1;
                break;
            case "escape":
                config["entries"]![0]!["name"] = "../outside.dz";
                break;
        }
        await metadata.SaveAsync(metadataDirectory, token);
        var output = Path.Combine(_root, "existing.kdz");
        await File.WriteAllBytesAsync(output, [5, 6, 7], token);

        var result = await handler.PackAsync(extracted, output, token);

        Assert.False(result.Success, result.Summary);
        Assert.Equal(new byte[] { 5, 6, 7 }, await File.ReadAllBytesAsync(output, token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("drive:name")]
    [InlineData("\u0003reserved")]
    public async Task BuildAsync_UnsafeName_DoesNotReplaceOutput(string name)
    {
        var entries = Inputs();
        entries[0] = entries[0] with { Name = name };
        var output = Path.Combine(_root, "existing.kdz");
        File.WriteAllBytes(output, [1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(entries, output,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task BuildAsync_DuplicateNamesOrLongName_RejectsWithoutPublishing()
    {
        var entries = Inputs();
        var output = Path.Combine(_root, "invalid.kdz");
        await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(
            [entries[0], entries[1] with { Name = "FIRMWARE.DZ" }], output,
            cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(
            [entries[0] with { Name = new string('a', 257) }], output,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task BuildAsync_SourceIsDestination_RejectsWithoutTruncation()
    {
        var entries = Inputs();
        var original = File.ReadAllBytes(entries[0].FilePath);

        await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(entries, entries[0].FilePath,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(original, File.ReadAllBytes(entries[0].FilePath));
    }

    [Fact]
    public async Task BuildAsync_MissingSecondSource_CleansTemporaryAndPreservesDestination()
    {
        var entries = Inputs();
        File.Delete(entries[1].FilePath);
        var output = Path.Combine(_root, "existing.kdz");
        File.WriteAllBytes(output, [7]);

        await Assert.ThrowsAsync<FileNotFoundException>(() => KdzBuilder.BuildAsync(entries, output,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task BuildAsync_CanceledToken_PreservesDestination()
    {
        var entries = Inputs();
        var output = Path.Combine(_root, "existing.kdz");
        File.WriteAllBytes(output, [7]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            KdzBuilder.BuildAsync(entries, output, cancellationToken: cancellation.Token));

        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Theory(Skip = "Set UOTAN_KDZTOOLS to a local kdztools checkout and provide Python on PATH.",
        SkipUnless = nameof(ToolsEnabled))]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BuildAsync_IndependentUnkdz_ExtractsMatchingHashes(bool reverse, bool repack)
    {
        var entries = Inputs();
        var payload = new byte[8193];
        new Random(1729).NextBytes(payload);
        File.WriteAllBytes(entries[0].FilePath, payload);
        File.WriteAllBytes(entries[1].FilePath, payload[..4097]);
        var output = Path.Combine(_root, "ours.kdz");
        var extracted = Path.Combine(_root, "independent");
        var token = TestContext.Current.CancellationToken;
        await KdzBuilder.BuildAsync(entries, output, payloadOrder: reverse ? [1, 0] : null, cancellationToken: token);
        using var context = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        if (repack)
        {
            var editable = Path.Combine(_root, "editable");
            var handler = new DzFormatHandler();
            var unpacked = await handler.UnpackAsync(output, editable, cancellationToken: token);
            Assert.True(unpacked.Success, unpacked.Summary);
            File.Delete(output);
            var replacement = new byte[16385];
            new Random(519).NextBytes(replacement);
            await File.WriteAllBytesAsync(Path.Combine(editable, entries[0].Name), replacement, token);
            await File.WriteAllBytesAsync(entries[0].FilePath, replacement, token);
            var packed = await handler.PackAsync(editable, output, token);
            Assert.True(packed.Success, packed.Summary);
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("python")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    Path.Combine(Environment.GetEnvironmentVariable("UOTAN_KDZTOOLS")!, "unkdz.py"),
                    "-f", output, "-x", "-d", extracted
                }
            }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        foreach (var entry in entries)
        {
            Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(entry.FilePath, token)),
                SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(extracted, entry.Name), token)));
        }
    }

    private KdzSource[] Inputs()
    {
        var first = Path.Combine(_root, "firmware.dz");
        var second = Path.Combine(_root, "helper.dll");
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(second, [5, 6, 7, 8, 9]);
        return [new("firmware.dz", first), new("helper.dll", second)];
    }

    private async Task<string> SourceWithGapsAsync()
    {
        var path = Path.Combine(_root, "source.kdz");
        await KdzBuilder.BuildAsync(Inputs(), path, payloadOrder: [1, 0],
            cancellationToken: TestContext.Current.CancellationToken);
        var source = File.ReadAllBytes(path);
        var gap = Enumerable.Range(0, 7).Select(value => (byte)(value + 0x70)).ToArray();
        var tail = Enumerable.Range(0, 11).Select(value => (byte)(value + 0x90)).ToArray();
        byte[] modified = [.. source[..4101], .. gap, .. source[4101..], .. tail];
        modified.AsSpan(1000, 100).Fill(0xAB);
        BinaryPrimitives.WriteUInt64LittleEndian(modified.AsSpan(8 + 264), 4108);
        File.WriteAllBytes(path, modified);
        return path;
    }

    private sealed class CancelingStream(CancellationTokenSource cancellation) : MemoryStream(new byte[128 * 1024])
    {
        public int ReadCount { get; private set; }
        public int LargestRequest { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            LargestRequest = Math.Max(LargestRequest, count);
            var read = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return read;
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}