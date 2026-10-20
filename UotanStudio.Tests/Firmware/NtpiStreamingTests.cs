using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.NTPi;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// NTPI streaming reader/writer tests: two-pass pack/unpack round-trip consistency,
/// bounded decompression rejection, and truncated-input rejection.
/// <para>NTPI 流式读写器测试：两遍打包/解包往返一致性、有界解压拒绝、截断输入拒绝。</para>
/// </summary>
public class NtpiStreamingTests
{
    [Fact]
    public void PackThenUnpack_RoundTripsAllFiles()
    {
        using var temp = new TempDirectory();
        var inputDir = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(inputDir);

        var files = new Dictionary<string, byte[]>
        {
            ["a.txt"] = Encoding.UTF8.GetBytes("hello world\n"),
            ["sub/b.bin"] = BuildRandom(64 * 1024, 12345),
            ["c.bin"] = BuildRandom(300_000, 777),
            ["sub/deep/d.txt"] = Encoding.UTF8.GetBytes("nested file content" + new string('x', 500))
        };
        foreach (var (relative, data) in files)
        {
            var full = Path.Combine(inputDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        }

        var ntpiPath = Path.Combine(temp.Path, "pkg.ntpi");
        using (var writer = NtpiWriter.CreateDefault())
        {
            writer.Pack(inputDir, ntpiPath);
        }
        Assert.True(File.Exists(ntpiPath));

        var outDir = Path.Combine(temp.Path, "out");
        using (var reader = NtpiReader.CreateDefault())
        {
            reader.Unpack(ntpiPath, outDir, cancellationToken: TestContext.Current.CancellationToken);
        }

        foreach (var (relative, expected) in files)
        {
            var extracted = Path.Combine(outDir, Path.GetFileName(relative));
            Assert.True(File.Exists(extracted), $"Missing extracted file '{relative}'.");
            Assert.Equal(expected, File.ReadAllBytes(extracted));
        }
    }

    [Fact]
    public void PackThenUnpack_EmptyFilesUseNoRegion6Storage()
    {
        using var temp = new TempDirectory();
        var inputDir = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(inputDir);
        File.WriteAllBytes(Path.Combine(inputDir, "empty-a.bin"), []);
        File.WriteAllBytes(Path.Combine(inputDir, "empty-b.bin"), []);

        var ntpiPath = Path.Combine(temp.Path, "empty.ntpi");
        using (var writer = NtpiWriter.CreateDefault())
        {
            writer.Pack(inputDir, ntpiPath);
        }

        using var reader = NtpiReader.CreateDefault();
        var info = reader.ReadInfo(ntpiPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(info.Regions, region => region.Type == RegionType.Region6 && region.Size == 0);
        Assert.All(info.FileEntries, entry =>
        {
            Assert.Equal(0, entry.OriginalLength);
            Assert.Equal(0, entry.Length);
            Assert.Equal(0, entry.Offset);
        });

        var parsed = reader.Parse(ntpiPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(parsed.Regions.TryGetValue(RegionType.Region6, out var region6) && region6.Length != 0);
        Assert.All(parsed.ExtractedFiles.Values, Assert.Empty);

        var outputDir = Path.Combine(temp.Path, "output");
        reader.Unpack(ntpiPath, outputDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(File.ReadAllBytes(Path.Combine(outputDir, "empty-a.bin")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(outputDir, "empty-b.bin")));
    }

    [Fact]
    public void PackThenParse_ReturnsAllRegionsAndExtractedFiles()
    {
        using var temp = new TempDirectory();
        var ntpiPath = PackSamplePackage(temp.Path, out var files, out _);

        using var reader = NtpiReader.CreateDefault();
        var parsed = reader.Parse(ntpiPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(files.Count, parsed.FileEntries.Count);
        Assert.Equal(files.Count, parsed.ExtractedFiles.Count);
        Assert.True(parsed.Regions.ContainsKey(RegionType.Metadata));
        Assert.True(parsed.Regions.ContainsKey(RegionType.KeyMap));
        Assert.True(parsed.Regions.ContainsKey(RegionType.FileIndex));
        Assert.True(parsed.Regions.ContainsKey(RegionType.Region6));
        Assert.Equal(1ul, parsed.Header.VersionMajor);

        foreach (var (relative, data) in files)
        {
            Assert.True(parsed.ExtractedFiles.TryGetValue(relative, out var extracted), $"Missing extracted entry '{relative}'.");
            Assert.Equal(data, extracted);
        }
    }

    [Fact]
    public void ReadInfo_RecordsRegionsAndFileEntriesWithoutExtracting()
    {
        using var temp = new TempDirectory();
        var ntpiPath = PackSamplePackage(temp.Path, out var files, out _);

        using var reader = NtpiReader.CreateDefault();
        var info = reader.ReadInfo(ntpiPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1ul, info.MajorVersion);
        Assert.Equal(3ul, info.MinorVersion);
        Assert.Equal(6, info.Regions.Count);
        Assert.Contains(info.Regions, r => r.Type == RegionType.Region6 && r.Size > 0);
        Assert.Equal(files.Count, info.FileEntries.Count);

        foreach (var entry in info.FileEntries)
        {
            Assert.True(entry.Offset >= 0, $"Entry '{entry.Name}' has a negative offset.");
            Assert.True(entry.OriginalLength > 0, $"Entry '{entry.Name}' has a non-positive original length.");
            Assert.True(entry.Length > 112, $"Entry '{entry.Name}' must be at least one NTENCODE block.");
            Assert.False(string.IsNullOrEmpty(entry.FileSha256Hash), $"Entry '{entry.Name}' is missing its hash.");
        }
    }

    [Fact]
    public void PackFromEntries_RoundTrips()
    {
        var entries = new List<FileEntry> { new() { Name = "one.bin" }, new() { Name = "two.bin" } };
        var data = new Dictionary<string, byte[]>
        {
            ["one.bin"] = BuildRandom(40_000, 11),
            ["two.bin"] = BuildRandom(90_000, 22)
        };

        using var temp = new TempDirectory();
        var ntpiPath = Path.Combine(temp.Path, "pkg.ntpi");
        using (var writer = NtpiWriter.CreateDefault())
        {
            writer.PackFromEntries(entries, data, ntpiPath);
        }

        using var reader = NtpiReader.CreateDefault();
        var parsed = reader.Parse(ntpiPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, parsed.FileEntries.Count);
        Assert.Equal(data["one.bin"], parsed.ExtractedFiles["one.bin"]);
        Assert.Equal(data["two.bin"], parsed.ExtractedFiles["two.bin"]);
    }

    [Fact]
    public void Pack_OutputInsideInput_DoesNotIncludeItself()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "source.txt"), "source");
        var package = Path.Combine(input, "package.ntpi");
        File.WriteAllText(package, "old package");

        using (var writer = NtpiWriter.CreateDefault())
        {
            writer.Pack(input, package);
        }

        using var reader = NtpiReader.CreateDefault();
        var info = reader.ReadInfo(package, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(info.FileEntries);
        Assert.Equal("source.txt", info.FileEntries[0].Name);
    }

    [Fact]
    public void Pack_WithLegacyByteArrayCryptoProvider_IsSupported()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "source.txt"), "source");
        var package = Path.Combine(temp.Path, "package.ntpi");

        using var writer = new NtpiWriter(new NonStreamingCryptoProvider(), new Lzma2Compressor());
        writer.Pack(input, package);

        Assert.True(new FileInfo(package).Length > 0);
    }

    [Fact]
    public void DefaultCryptoFallback_UsesLegacyByteArrayProvider()
    {
        ICryptoProvider provider = new NonStreamingCryptoProvider();
        var data = BuildRandom(1024, 19);
        using var decrypt = provider.CreateDecryptStream(
            new MemoryStream(data), new byte[32], new byte[16]);
        using var plaintext = new MemoryStream();
        decrypt.CopyTo(plaintext);
        Assert.Equal(data, plaintext.ToArray());

        using var ciphertext = new MemoryStream();
        using (var encrypt = provider.CreateEncryptStream(ciphertext, new byte[32], new byte[16]))
        {
            encrypt.Write(data);
        }
        Assert.Equal(data, ciphertext.ToArray());
    }

    [Fact]
    public void DefaultCompressorFallback_UsesLegacyByteArrayProvider_AndChecksOutputBound()
    {
        ILzma2Compressor compressor = new NonStreamingCompressor();
        var data = new byte[1024];
        var encoded = new Lzma2Compressor().Compress(data, (ulong)data.Length);
        using var input = new MemoryStream(encoded);
        using var output = new MemoryStream();
        compressor.Decompress(input, output, data.Length);
        Assert.Equal(data, output.ToArray());

        input.Position = 0;
        Assert.Throws<InvalidDataException>(() =>
            compressor.Decompress(input, new MemoryStream(), data.Length - 1));
    }

    [Fact]
    public void DefaultCompressorFallback_AcceptsOnlyShortZeroAesPadding()
    {
        ILzma2Compressor compressor = new NonStreamingCompressor();
        var data = BuildRandom(1024, 91);
        var encoded = new Lzma2Compressor().Compress(data, (ulong)data.Length);

        using (var paddedInput = new MemoryStream([.. encoded, .. new byte[15]]))
        using (var output = new MemoryStream())
        {
            compressor.Decompress(paddedInput, output, data.Length);
            Assert.Equal(data, output.ToArray());
        }

        using var tooMuchPadding = new MemoryStream([.. encoded, .. new byte[16]]);
        Assert.Throws<InvalidDataException>(() => compressor.Decompress(tooMuchPadding, new MemoryStream(), data.Length));

        using var nonZeroPadding = new MemoryStream([.. encoded, 1]);
        Assert.Throws<InvalidDataException>(() => compressor.Decompress(nonZeroPadding, new MemoryStream(), data.Length));
    }

    [Fact]
    public void DefaultCompressorFallback_RejectsShortOutput()
    {
        ILzma2Compressor compressor = new ShortOutputCompressor();
        var encoded = new Lzma2Compressor().Compress(BuildRandom(64, 92), 64);
        using var input = new MemoryStream(encoded);
        Assert.Throws<InvalidDataException>(() => compressor.Decompress(input, new MemoryStream(), 64));
    }

    [Fact]
    public void Decompress_StreamOverload_RoundTrips()
    {
        var compressor = new Lzma2Compressor();
        var data = BuildRandom(120_000, 42);

        using var input = new MemoryStream(data);
        using var compressed = new MemoryStream();
        compressor.Compress(input, compressed, (ulong)data.Length);

        using var output = new MemoryStream();
        compressed.Position = 0;
        compressor.Decompress(compressed, output, data.Length);

        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void Compress_RejectsDeclaredSizeMismatch_AndAcceptsBoundedSubrange()
    {
        var compressor = new Lzma2Compressor();
        var data = BuildRandom(4096, 73);

        using (var shortInput = new MemoryStream(data[..^1]))
        using (var output = new MemoryStream())
        {
            Assert.Throws<Lzma2CompressionException>(() =>
                compressor.Compress(shortInput, output, (ulong)data.Length));
        }

        using (var extraInput = new MemoryStream(data))
        using (var output = new MemoryStream())
        {
            Assert.Throws<Lzma2CompressionException>(() =>
                compressor.Compress(extraInput, output, (ulong)(data.Length - 1)));
        }

        using var containingInput = new MemoryStream(data);
        using var bounded = new BoundedReadStream(containingInput, data.Length - 1, leaveOpen: true);
        using var compressed = new MemoryStream();
        compressor.Compress(bounded, compressed, (ulong)bounded.Length);

        compressed.Position = 0;
        using var roundTrip = new MemoryStream();
        compressor.Decompress(compressed, roundTrip, bounded.Length);
        Assert.Equal(data[..^1], roundTrip.ToArray());
    }

    [Fact]
    public void Decompress_RejectsOutputBeyondMaximumBound()
    {
        var compressor = new Lzma2Compressor();
        var data = BuildRandom(200_000, 7);

        using var input = new MemoryStream(data);
        using var compressed = new MemoryStream();
        compressor.Compress(input, compressed, (ulong)data.Length);

        compressed.Position = 0;
        using var output = new MemoryStream();
        Assert.Throws<Lzma2CompressionException>(() =>
            compressor.Decompress(compressed, output, data.Length - 1));
    }

    [Fact]
    public void Decompress_RejectsDeclaredOversizeBeforeDecoding_AndRejectsShortOutput()
    {
        var compressor = new Lzma2Compressor();
        var data = BuildRandom(4096, 93);
        var encoded = compressor.Compress(data, (ulong)data.Length);

        var beyondMaximum = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(beyondMaximum.AsSpan(32), (ulong)data.Length + 1);
        using (var input = new MemoryStream(beyondMaximum))
        {
            Assert.Throws<Lzma2CompressionException>(() =>
                compressor.Decompress(input, new MemoryStream(), data.Length));
        }

        var beyondLong = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(beyondLong.AsSpan(32), (ulong)long.MaxValue + 1);
        using (var input = new MemoryStream(beyondLong))
        {
            Assert.Throws<Lzma2CompressionException>(() =>
                compressor.Decompress(input, new MemoryStream(), long.MaxValue));
        }

        var declaredLonger = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(declaredLonger.AsSpan(32), (ulong)data.Length + 1);
        using (var input = new MemoryStream(declaredLonger))
        {
            Assert.Throws<Lzma2CompressionException>(() =>
                compressor.Decompress(input, new MemoryStream(), data.Length + 1));
        }
    }

    [Fact]
    public void Decompress_ByteArrayRejectsImpossibleOriginalSizeBeforeDecoding()
    {
        var compressor = new Lzma2Compressor();
        var encoded = compressor.Compress(BuildRandom(64, 94), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(encoded.AsSpan(32), (ulong)int.MaxValue + 1);

        Assert.Throws<Lzma2CompressionException>(() => compressor.Decompress(encoded));
    }

    [Fact]
    public void Decompress_TruncatedStream_Throws()
    {
        var compressor = new Lzma2Compressor();
        var data = BuildRandom(150_000, 13);

        using var input = new MemoryStream(data);
        using var compressed = new MemoryStream();
        compressor.Compress(input, compressed, (ulong)data.Length);

        var compressedBytes = compressed.ToArray();
        var truncated = compressedBytes[..(compressedBytes.Length - 24)];

        using var truncatedInput = new MemoryStream(truncated);
        using var output = new MemoryStream();
        Assert.ThrowsAny<Exception>(() => compressor.Decompress(truncatedInput, output, data.Length));
    }

    [Fact]
    public void Unpack_TruncatedPackageFile_Throws()
    {
        using var temp = new TempDirectory();
        var ntpiPath = PackSamplePackage(temp.Path, out _, out _);

        var full = File.ReadAllBytes(ntpiPath);
        var truncatedPath = Path.Combine(temp.Path, "trunc.ntpi");
        File.WriteAllBytes(truncatedPath, full[..(int)(full.Length * 0.7)]);

        using var reader = NtpiReader.CreateDefault();
        Assert.ThrowsAny<NtpiException>(() => reader.Unpack(truncatedPath, Path.Combine(temp.Path, "out"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Parse_TruncatedPackageFile_Throws()
    {
        using var temp = new TempDirectory();
        var ntpiPath = PackSamplePackage(temp.Path, out _, out _);

        var full = File.ReadAllBytes(ntpiPath);
        var truncatedPath = Path.Combine(temp.Path, "trunc2.ntpi");
        File.WriteAllBytes(truncatedPath, full[..(int)(full.Length * 0.5)]);

        using var reader = NtpiReader.CreateDefault();
        Assert.ThrowsAny<NtpiException>(() => reader.Parse(truncatedPath, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static string PackSamplePackage(string tempRoot, out Dictionary<string, byte[]> files, out string inputDir)
    {
        files = new Dictionary<string, byte[]>
        {
            ["alpha.bin"] = BuildRandom(48_000, 100),
            ["beta.txt"] = Encoding.UTF8.GetBytes("beta content padded " + new string('y', 256)),
            ["gamma.bin"] = BuildRandom(16_000, 200)
        };

        inputDir = Path.Combine(tempRoot, "sample-input");
        Directory.CreateDirectory(inputDir);
        foreach (var (relative, data) in files)
        {
            File.WriteAllBytes(Path.Combine(inputDir, relative), data);
        }

        var ntpiPath = Path.Combine(tempRoot, "sample.ntpi");
        using (var writer = NtpiWriter.CreateDefault())
        {
            writer.Pack(inputDir, ntpiPath);
        }
        return ntpiPath;
    }

    private sealed class NonStreamingCryptoProvider : ICryptoProvider
    {
        public byte[] Decrypt(byte[] data, byte[] key, byte[] iv) => data;
        public byte[] Encrypt(byte[] data, byte[] key, byte[] iv) => data;
    }

    private sealed class NonStreamingCompressor : ILzma2Compressor
    {
        public byte[] Decompress(byte[] data) => new Lzma2Compressor().Decompress(data);
        public byte[] Compress(byte[] data, ulong originalSize) => data;
    }

    private sealed class ShortOutputCompressor : ILzma2Compressor
    {
        public byte[] Decompress(byte[] data) => new byte[63];
        public byte[] Compress(byte[] data, ulong originalSize) => data;
    }

    private static byte[] BuildRandom(int size, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[size];
        random.NextBytes(bytes);
        return bytes;
    }
}